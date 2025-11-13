using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TiYf.Engine.Core;
using TiYf.Engine.Core.Instruments;
using TiYf.Engine.Sidecar;
using TiYf.Engine.Sim;
using TiYf.Engine.Core.Slippage;
using TiYf.Engine.Host.News;

namespace TiYf.Engine.Host;

internal sealed class EngineLoopService : BackgroundService
{
    private readonly EngineHostState _state;
    private readonly EngineHostConfiguration _configuration;
    private readonly OandaAdapterSettings _adapterSettings;
    private readonly OandaStreamSettings _streamSettings;
    private readonly ILogger<EngineLoopService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EngineHostOptions _hostOptions;
    private readonly IConnectableExecutionAdapter? _executionAdapter;
    private FileIdempotencyPersistence? _idempotencyPersistence;
    private StartupReconciliationRunner? _startupReconciliationRunner;
    private NewsFeedRunner? _newsFeedRunner;

    private LiveTickSource? _tickSource;
    private EngineLoop? _engineLoop;
    private HostJournalWriter? _journalWriter;
    private TradesJournalWriter? _tradesWriter;
    private PositionTracker? _positionTracker;
    private EngineConfig? _engineConfig;
    private string? _configDirectory;
    private string? _primaryInstrument;
    private readonly IReadOnlyList<(string Label, BarInterval Interval)> _configuredTimeframes;
    private readonly Dictionary<long, string> _timeframeLabelByTicks;
    private readonly string _snapshotPath;
    private readonly TimeSpan _decisionSkewTolerance;
    private readonly string _snapshotSource;
    private readonly string _schemaVersion = TiYf.Engine.Core.Infrastructure.Schema.Version;
    private InMemoryBarKeyTracker? _barKeyTracker;
    private string _engineInstanceId = string.Empty;
    private DateTime _loopStartUtc;
    private readonly object _snapshotSync = new();
    private readonly TimeSpan _reconciliationInterval = TimeSpan.FromMinutes(5);
    private DateTime _nextReconciliationUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);

    private Task? _engineTask;
    private Task? _heartbeatTask;
    private ReconciliationJournalWriter? _reconciliationJournal;
    private ReconciliationTelemetry? _reconciliationTelemetry;

    private long _riskEvents;
    private long _alerts;
    private int _lastOpenPositions;
    private int _lastActiveOrders;
    private int _consecutiveFailures;
    private static readonly DateTime TimestampSentinel = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

    public EngineLoopService(
        EngineHostState state,
        EngineHostConfiguration configuration,
        OandaAdapterSettings adapterSettings,
        OandaStreamSettings streamSettings,
        ILogger<EngineLoopService> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<EngineHostOptions> hostOptions,
        IConnectableExecutionAdapter? executionAdapter = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _adapterSettings = adapterSettings ?? throw new ArgumentNullException(nameof(adapterSettings));
        _streamSettings = streamSettings ?? throw new ArgumentNullException(nameof(streamSettings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _hostOptions = hostOptions?.Value ?? throw new ArgumentNullException(nameof(hostOptions));
        _configuredTimeframes = ParseTimeframes(_hostOptions.Timeframes);
        if (_configuredTimeframes.Count == 0)
        {
            _configuredTimeframes = new List<(string Label, BarInterval Interval)>(new[] { ("H1", BarInterval.OneHour) });
        }
        _timeframeLabelByTicks = _configuredTimeframes.ToDictionary(tf => tf.Interval.Duration.Ticks, tf => tf.Label);
        _snapshotPath = ResolveSnapshotPath(_hostOptions.SnapshotPath);
        _decisionSkewTolerance = TimeSpan.FromMilliseconds(Math.Max(0, _hostOptions.DecisionSkewToleranceMilliseconds));
        _snapshotSource = string.Equals(_streamSettings.FeedMode, "replay", StringComparison.OrdinalIgnoreCase) ? "replay" : "live";
        _executionAdapter = executionAdapter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_hostOptions.EnableContinuousLoop)
        {
            _logger.LogInformation("Engine loop disabled via configuration; skipping startup.");
            return;
        }

        if (!_streamSettings.Enable || !_hostOptions.EnableStreamingFeed)
        {
            _logger.LogInformation("Streaming feed disabled (stream_enable={Enable}, option={Option})", _streamSettings.Enable, _hostOptions.EnableStreamingFeed);
            return;
        }

        _loopStartUtc = DateTime.UtcNow;
        _state.SetLoopStart(_loopStartUtc);
        _state.SetTimeframes(_configuredTimeframes.Select(tf => tf.Label));
        _state.UpdateNewsTelemetry(null, 0, false, null, null);

        LoadSnapshot();

        _state.UpdateStreamConnection(false);
        _state.RecordStreamHeartbeat(DateTime.UtcNow);
        UpdateHostMetrics();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            await InitializeRuntimeAsync(linkedCts.Token).ConfigureAwait(false);
            if (_engineLoop is null || _tickSource is null)
            {
                _logger.LogWarning("Streaming runtime initialization failed; loop not created.");
                return;
            }

            _engineTask = Task.Run(() => _engineLoop.RunAsync(linkedCts.Token), CancellationToken.None);
            _heartbeatTask = MonitorHeartbeatAsync(linkedCts.Token);

            if (string.Equals(_streamSettings.FeedMode, "replay", StringComparison.OrdinalIgnoreCase))
            {
                await RunReplayAsync(linkedCts.Token).ConfigureAwait(false);
            }
            else
            {
                await RunLiveAsync(linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Streaming service encountered a fatal error");
            throw;
        }
        finally
        {
            linkedCts.Cancel();
            _tickSource?.Complete();

            if (_engineTask is not null)
            {
                try { await _engineTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger.LogWarning(ex, "Engine loop terminated with error"); }
            }

            if (_heartbeatTask is not null)
            {
                try { await _heartbeatTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            await DisposeRuntimeAsync().ConfigureAwait(false);
            PersistSnapshot();
            _state.UpdateStreamConnection(false);
        }
    }

    private async Task InitializeRuntimeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (config, _, raw) = EngineConfigLoader.Load(_configuration.ConfigPath);
        using var rawDoc = raw;

        _engineConfig = config;
        _configDirectory = Path.GetDirectoryName(_configuration.ConfigPath) ?? Directory.GetCurrentDirectory();

        var runId = ResolveRunId(config);
        var journalRoot = ResolveJournalRoot(config, _configDirectory);
        var schemaVersion = !string.IsNullOrWhiteSpace(config.SchemaVersion)
            ? config.SchemaVersion
            : TiYf.Engine.Core.Infrastructure.Schema.Version;

        var instruments = LoadInstrumentCatalog(config, rawDoc, _configDirectory);
        _primaryInstrument = instruments.All().FirstOrDefault()?.Id.Value;

        var intervals = _configuredTimeframes.Select(tf => tf.Interval).Distinct().ToList();
        if (intervals.Count == 0)
        {
            intervals.Add(BarInterval.OneHour);
        }

        var builders = CreateBuilders(instruments.All(), intervals);
        var tracker = _barKeyTracker ??= new InMemoryBarKeyTracker();
        _tickSource = new LiveTickSource();
        _positionTracker = new PositionTracker();

        var clock = new SystemClock();
        var riskFormulas = new RiskFormulas();
        var basketAggregator = new BasketRiskAggregator();
        var riskConfig = ResolveRiskConfig(rawDoc);
        var riskConfigHash = riskConfig?.RiskConfigHash ?? string.Empty;
        _state.SetRiskConfigHash(riskConfigHash);
        var promotionConfig = riskConfig?.Promotion;
        _state.SetPromotionConfig(promotionConfig);
        var newsEvents = LoadNewsEvents(riskConfig?.NewsBlackout);
        var riskMode = ResolveRiskMode(rawDoc);
        var dataVersion = ComputeDataVersion(rawDoc, _configDirectory);

        var slippageProfile = config.Slippage;
        var slippageName = SlippageModelFactory.Normalize(slippageProfile?.Model ?? config.SlippageModel);
        var slippageModel = SlippageModelFactory.Create(slippageProfile, slippageName);
        _state.SetSlippageModel(slippageName);
        _state.UpdateIdempotencyMetrics(0, 0, 0);

        var journalWriter = new FileJournalWriter(
            journalRoot,
            runId,
            schemaVersion,
            _configuration.ConfigHash,
            _state.Adapter,
            string.IsNullOrWhiteSpace(config.BrokerId) ? "oanda" : config.BrokerId,
            string.IsNullOrWhiteSpace(config.AccountId) ? (_adapterSettings.AccountId ?? "unknown") : config.AccountId,
            dataVersion);

        _journalWriter = new HostJournalWriter(journalWriter, OnJournalEvent);
        _tradesWriter = new TradesJournalWriter(journalRoot, runId, schemaVersion, _configuration.ConfigHash, _state.Adapter, dataVersion);
        var idempotencyPath = Path.Combine(journalRoot, "idempotency", _state.Adapter, "keys.jsonl");
        _idempotencyPersistence = new FileIdempotencyPersistence(
            idempotencyPath,
            TimeSpan.FromHours(24),
            EngineLoop.OrderIdempotencyCapacity,
            EngineLoop.CancelIdempotencyCapacity,
            _logger);
        var persistedIdempotency = _idempotencyPersistence.Load();
        var persistedLoaded = persistedIdempotency.Orders.Count + persistedIdempotency.Cancels.Count;
        _state.SetIdempotencyPersistenceStats(persistedLoaded, persistedIdempotency.ExpiredDropped, persistedIdempotency.LoadedUtc);
        var reconcileRoot = Path.Combine(journalRoot, "reconcile");
        var accountId = string.IsNullOrWhiteSpace(config.AccountId)
            ? (_adapterSettings.AccountId ?? "unknown")
            : config.AccountId;
        _reconciliationJournal = new ReconciliationJournalWriter(reconcileRoot, _state.Adapter, runId, _configuration.ConfigHash, accountId);
        _reconciliationTelemetry = new ReconciliationTelemetry(
            () => _positionTracker?.SnapshotOpenPositions() ?? Array.Empty<(string, TradeSide, decimal, long, DateTime)>(),
            CreateBrokerSnapshotProvider(),
            _reconciliationJournal,
            _state,
            _logger);
        _startupReconciliationRunner = new StartupReconciliationRunner(
            () => DateTime.UtcNow,
            (utc, token) => _reconciliationTelemetry.EmitAsync(utc, token),
            _logger);
        _nextReconciliationUtc = DateTime.UtcNow;

        var strategyStart = AlignToMinute(DateTime.UtcNow);
        var strategy = new DeterministicScriptStrategy(clock, instruments.All(), strategyStart);
        var riskEnforcer = new RiskEnforcer(riskFormulas, basketAggregator, schemaVersion, _configuration.ConfigHash);

        _engineLoop = new EngineLoop(
            clock,
            builders,
            tracker,
            _journalWriter,
            _tickSource,
            config.BarOutputEventType ?? "BAR_V1",
            OnBarEmitted,
            OnPositionMetrics,
            riskFormulas,
            basketAggregator,
            _configuration.ConfigHash,
            schemaVersion,
            riskEnforcer,
            riskConfig,
            deterministicStrategy: strategy,
            execution: _executionAdapter,
            positions: _positionTracker,
            tradesWriter: _tradesWriter,
            dataVersion: dataVersion,
            sourceAdapter: _state.Adapter,
            slippageModel: slippageModel,
            riskMode: riskMode,
            riskConfigHash: riskConfigHash,
            newsEvents: newsEvents,
            timeframeLabels: _timeframeLabelByTicks,
            riskGateCallback: (gate, throttled) => _state.RegisterRiskGateEvent(gate, throttled),
            gvrsSnapshotCallback: snapshot => _state.SetGvrsSnapshot(snapshot),
            orderAcceptedCallback: (symbol, units) =>
            {
                _state.RegisterOrderAccepted(symbol, units);
                UpdateHostMetrics();
            },
            orderRejectedCallback: () =>
            {
                _state.RegisterOrderRejected();
                UpdateHostMetrics();
            },
            idempotencyMetricsCallback: (orderCache, cancelCache, evictions) =>
            {
                _state.UpdateIdempotencyMetrics(orderCache, cancelCache, evictions);
            },
            warnCallback: message => _logger.LogWarning("{Message}", message),
            slippageMetricsCallback: delta =>
            {
                _state.RecordSlippage(delta);
            },
            idempotencyPersistence: _idempotencyPersistence,
            persistedIdempotencySnapshot: persistedIdempotency);

        await StartNewsFeedMonitorAsync(riskConfig?.NewsBlackout).ConfigureAwait(false);

        _logger.LogInformation("Streaming runtime initialized run_id={RunId} journal={Journal}", runId, journalWriter.RunDirectory);
        _state.SetLastLog($"stream:run_id={runId}");
        UpdateHostMetrics();
        if (_startupReconciliationRunner is not null)
        {
            await _startupReconciliationRunner.RunOnceAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DisposeRuntimeAsync()
    {
        if (_tradesWriter is not null)
        {
            await _tradesWriter.DisposeAsync().ConfigureAwait(false);
            _tradesWriter = null;
        }

        if (_journalWriter is not null)
        {
            await _journalWriter.DisposeAsync().ConfigureAwait(false);
            _journalWriter = null;
        }
        if (_reconciliationJournal is not null)
        {
            await _reconciliationJournal.DisposeAsync().ConfigureAwait(false);
            _reconciliationJournal = null;
        }

        if (_newsFeedRunner is not null)
        {
            await _newsFeedRunner.DisposeAsync().ConfigureAwait(false);
            _newsFeedRunner = null;
        }

        _tickSource?.Dispose();
        _tickSource = null;
        _engineLoop = null;
        _positionTracker = null;
        _idempotencyPersistence = null;
        _startupReconciliationRunner = null;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            PersistSnapshot();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist loop snapshot during StopAsync.");
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunLiveAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting OANDA live stream (mode={Mode})", _adapterSettings.Mode);
        await using var adapter = new OandaStreamFeedAdapter(
            _httpClientFactory.CreateClient("oanda-stream"),
            _adapterSettings,
            _streamSettings,
            logWarning: message => _logger.LogWarning("{Message}", message),
            logError: (exception, message) => _logger.LogError(exception, "{Message}", message));

        await adapter.RunAsync(
            evt => HandleStreamEventAsync(evt, ct),
            OnStreamConnectedAsync,
            OnStreamDisconnectedAsync,
            ct).ConfigureAwait(false);
    }

    private async Task RunReplayAsync(CancellationToken ct)
    {
        var path = ResolveReplayPath();
        if (path is null || !File.Exists(path))
        {
            _logger.LogWarning("Replay ticks file not found path={Path}", path ?? "<unset>");
            return;
        }

        if (string.IsNullOrWhiteSpace(_primaryInstrument))
        {
            _logger.LogWarning("Replay mode requires instrument catalog; primary instrument unresolved.");
            return;
        }

        _logger.LogInformation("Starting replay stream ticks={Path}", path);
        _state.UpdateStreamConnection(true);

        var replayLines = File.ReadLines(path)
            .Skip(1)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (replayLines.Length == 0)
        {
            _logger.LogWarning("Replay ticks file is empty path={Path}", path);
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            foreach (var line in replayLines)
            {
                ct.ThrowIfCancellationRequested();
                var parts = line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2) continue;
                if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts))
                {
                    continue;
                }
                if (!decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var price))
                {
                    continue;
                }

                var utc = DateTime.SpecifyKind(ts, DateTimeKind.Utc);
                var tick = new PriceTick(new InstrumentId(_primaryInstrument), utc, price, 0m);
                _tickSource?.Enqueue(tick);
                _state.RecordStreamHeartbeat(utc);
                _state.UpdateLag(Math.Max(0d, (DateTime.UtcNow - utc).TotalMilliseconds));

                await Task.Delay(TimeSpan.FromMilliseconds(40), ct).ConfigureAwait(false);
            }
        }
    }

    private Task HandleStreamEventAsync(OandaStreamEvent evt, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        if (evt.IsHeartbeat)
        {
            if (IsSentinelTimestamp(evt.Timestamp))
            {
                return Task.CompletedTask;
            }
            _state.RecordStreamHeartbeat(evt.Timestamp);
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(evt.Instrument))
        {
            return Task.CompletedTask;
        }

        var normalizedInstrument = NormalizeInstrument(evt.Instrument);
        if (string.IsNullOrWhiteSpace(normalizedInstrument))
        {
            return Task.CompletedTask;
        }

        if (IsSentinelTimestamp(evt.Timestamp))
        {
            return Task.CompletedTask;
        }

        var mid = ComputeMid(evt.Bid, evt.Ask);
        if (mid <= 0m)
        {
            return Task.CompletedTask;
        }

        var tick = new PriceTick(new InstrumentId(normalizedInstrument), evt.Timestamp, mid, 0m);
        _tickSource?.Enqueue(tick);
        _state.RecordStreamHeartbeat(evt.Timestamp);
        _state.UpdateLag(Math.Max(0d, (DateTime.UtcNow - evt.Timestamp).TotalMilliseconds));

        return Task.CompletedTask;
    }

    private Task OnStreamConnectedAsync()
    {
        _consecutiveFailures = 0;
        _state.UpdateStreamConnection(true);
        _state.RecordStreamHeartbeat(DateTime.UtcNow);
        _logger.LogInformation("OANDA stream connected");
        return Task.CompletedTask;
    }

    private Task OnStreamDisconnectedAsync()
    {
        _state.UpdateStreamConnection(false);
        _consecutiveFailures++;
        _logger.LogWarning("OANDA stream disconnected (consecutive_failures={Failures})", _consecutiveFailures);

        if (_consecutiveFailures >= _hostOptions.StreamAlertThreshold)
        {
            Interlocked.Increment(ref _alerts);
            _state.IncrementAlertCounter();
            _consecutiveFailures = 0;
            UpdateHostMetrics();
        }

        return Task.CompletedTask;
    }

    private async Task MonitorHeartbeatAsync(CancellationToken ct)
    {
        var interval = _hostOptions.StreamStaleThreshold > TimeSpan.Zero
            ? TimeSpan.FromSeconds(Math.Max(1, _hostOptions.StreamStaleThreshold.TotalSeconds / 2))
            : TimeSpan.FromSeconds(5);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            var snapshot = _state.CreateMetricsSnapshot();
            if (snapshot.StreamConnected == 1 &&
                snapshot.StreamHeartbeatAgeSeconds > _hostOptions.StreamStaleThreshold.TotalSeconds)
            {
                _logger.LogWarning("Stream heartbeat stale (age={Age:F1}s)", snapshot.StreamHeartbeatAgeSeconds);
                _state.UpdateStreamConnection(false);
                Interlocked.Increment(ref _alerts);
                _state.IncrementAlertCounter();
                UpdateHostMetrics();
            }

            await TryEmitReconciliationAsync(ct).ConfigureAwait(false);
        }
    }

    private void LoadSnapshot()
    {
        try
        {
            var existed = File.Exists(_snapshotPath);
            var snapshot = LoopSnapshotPersistence.Load(_snapshotPath);
            if (!string.Equals(snapshot.SchemaVersion, _schemaVersion, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Loop snapshot schema mismatch found={Found} expected={Expected}; ignoring snapshot at {Path}.", snapshot.SchemaVersion, _schemaVersion, _snapshotPath);
                ResetLoopState();
                return;
            }

            _barKeyTracker = snapshot.Tracker ?? new InMemoryBarKeyTracker();
            var decisionSnapshot = snapshot.DecisionsByTimeframe ?? new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            _state.BootstrapLoopState(snapshot.DecisionsTotal, snapshot.LoopIterationsTotal, snapshot.LastDecisionUtc, decisionSnapshot);
            if (!string.IsNullOrWhiteSpace(snapshot.EngineInstanceId))
            {
                _engineInstanceId = snapshot.EngineInstanceId;
            }

            var barCount = _barKeyTracker.Snapshot().Count();
            if (!existed || (barCount == 0 && snapshot.DecisionsTotal == 0 && snapshot.LoopIterationsTotal == 0))
            {
                _logger.LogInformation("No prior loop snapshot found; starting fresh snapshot at {Path}.", _snapshotPath);
            }
            else
            {
                _logger.LogInformation("Loop snapshot loaded source={Source} bars={BarCount} decisions_total={Decisions} last_decision={LastDecision:o}", snapshot.Source, barCount, snapshot.DecisionsTotal, snapshot.LastDecisionUtc);
            }
            if (!string.Equals(snapshot.Source, _snapshotSource, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Loop snapshot source differs (snapshot={SnapshotSource}, current={CurrentSource}); continuing.", snapshot.Source, _snapshotSource);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load engine loop snapshot; starting from clean state.");
            ResetLoopState();
        }

        if (string.IsNullOrWhiteSpace(_engineInstanceId))
        {
            _engineInstanceId = GenerateEngineInstanceId();
        }
    }

    private void PersistSnapshot()
    {
        if (_barKeyTracker is null)
        {
            return;
        }

        var loopData = _state.GetLoopSnapshotData();
        lock (_snapshotSync)
        {
            var snapshot = new LoopSnapshot(
                _schemaVersion,
                _engineInstanceId,
                _snapshotSource,
                _barKeyTracker,
                loopData.DecisionsTotal,
                loopData.LoopIterationsTotal,
                loopData.LastDecisionUtc,
                loopData.DecisionsByTimeframe);
            try
            {
                LoopSnapshotPersistence.Save(_snapshotPath, snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist loop snapshot to {Path}.", _snapshotPath);
            }
        }
    }

    private void OnBarEmitted(Bar bar)
    {
        var now = DateTime.UtcNow;
        _state.UpdateLag(Math.Max(0d, (now - bar.EndUtc).TotalMilliseconds));

        var label = ResolveTimeframeLabel(bar.EndUtc - bar.StartUtc);
        _state.RecordLoopDecision(label, bar.EndUtc);

        if (_decisionSkewTolerance > TimeSpan.Zero)
        {
            var skew = Math.Abs((now - bar.EndUtc).TotalMilliseconds);
            if (skew > _decisionSkewTolerance.TotalMilliseconds)
            {
                _logger.LogWarning("Decision skew {SkewMs:F0}ms exceeded tolerance {ToleranceMs:F0}ms timeframe={Timeframe} bar_end={EndUtc:o}", skew, _decisionSkewTolerance.TotalMilliseconds, label, bar.EndUtc);
            }
        }

        PersistSnapshot();
        UpdateHostMetrics();
    }

    private void ResetLoopState()
    {
        _barKeyTracker = new InMemoryBarKeyTracker();
        _state.BootstrapLoopState(0, 0, null, new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase));
    }

    private void OnPositionMetrics(int openPositions, int activeOrders)
    {
        _lastOpenPositions = openPositions < 0 ? 0 : openPositions;
        _lastActiveOrders = activeOrders < 0 ? 0 : activeOrders;
        _state.UpdatePendingOrders(_lastActiveOrders);
        UpdateHostMetrics();
    }

    private void OnJournalEvent(string eventType)
    {
        if (eventType.StartsWith("ALERT_", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _alerts);
            _state.IncrementAlertCounter();
            if (TryMapRiskGate(eventType, out var gate, out var throttled))
            {
                _state.RegisterRiskGateEvent(gate, throttled);
            }
        }
        if (eventType.StartsWith("INFO_RISK_", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _riskEvents);
        }
        UpdateHostMetrics();
    }

    private static bool TryMapRiskGate(string eventType, out string gate, out bool throttled)
    {
        throttled = false;
        gate = string.Empty;
        switch (eventType)
        {
            case "ALERT_BLOCK_SESSION_WINDOW":
                gate = "session_window";
                return true;
            case "ALERT_BLOCK_DAILY_LOSS_CAP":
                gate = "daily_loss_cap";
                return true;
            case "ALERT_BLOCK_GLOBAL_DRAWDOWN":
            case "ALERT_BLOCK_DRAWDOWN":
                gate = "global_drawdown";
                return true;
            case "ALERT_BLOCK_NEWS_BLACKOUT":
                gate = "news_blackout";
                return true;
            case "ALERT_BLOCK_DAILY_GAIN_CAP":
                gate = "daily_gain_cap";
                return true;
            case "ALERT_THROTTLE_DAILY_GAIN_CAP":
                gate = "daily_gain_cap";
                throttled = true;
                return true;
            default:
                return false;
        }
    }

    private void UpdateHostMetrics()
    {
        var alerts = Interlocked.Read(ref _alerts);
        var riskEvents = Interlocked.Read(ref _riskEvents);
        _state.SetMetrics(_lastOpenPositions, _lastActiveOrders, riskEvents, alerts);
    }

    private string? ResolveReplayPath()
    {
        if (_streamSettings.ReplayTicksFile is not null)
        {
            return ResolvePath(_streamSettings.ReplayTicksFile);
        }

        if (_engineConfig is not null && !string.IsNullOrWhiteSpace(_engineConfig.InputTicksFile))
        {
            return ResolvePath(_engineConfig.InputTicksFile);
        }

        return null;
    }

    private string ResolveRunId(EngineConfig config)
    {
        var rawRunId = string.IsNullOrWhiteSpace(config.RunId)
            ? $"RUN-OANDA-STREAM-{DateTime.UtcNow:yyyyMMddHHmmss}"
            : config.RunId.Trim();
        return rawRunId.Replace(" ", "-", StringComparison.Ordinal);
    }

    private string ResolveJournalRoot(EngineConfig config, string configDir)
    {
        if (!string.IsNullOrWhiteSpace(config.JournalRoot))
        {
            return Path.IsPathRooted(config.JournalRoot)
                ? config.JournalRoot
                : Path.GetFullPath(Path.Combine(configDir, config.JournalRoot));
        }

        return Path.Combine(configDir, "journals");
    }

    private Func<CancellationToken, Task<BrokerAccountSnapshot?>> CreateBrokerSnapshotProvider()
    {
        if (_executionAdapter is IBrokerAccountSnapshotProvider provider)
        {
            return async token => (BrokerAccountSnapshot?)await provider.GetBrokerAccountSnapshotAsync(token).ConfigureAwait(false);
        }

        return _ => Task.FromResult<BrokerAccountSnapshot?>(null);
    }

    private async Task TryEmitReconciliationAsync(CancellationToken ct)
    {
        if (_reconciliationTelemetry is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now < _nextReconciliationUtc)
        {
            return;
        }

        if (!await _reconcileLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            _nextReconciliationUtc = now + _reconciliationInterval;
            await _reconciliationTelemetry.EmitAsync(now, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Reconciliation snapshot emission canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit reconciliation snapshot.");
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private InMemoryInstrumentCatalog LoadInstrumentCatalog(EngineConfig config, JsonDocument raw, string configDir)
    {
        string? instrumentFile = config.InstrumentFile;
        if (string.IsNullOrWhiteSpace(instrumentFile))
        {
            if (raw.RootElement.TryGetProperty("InstrumentFile", out var legacy) && legacy.ValueKind == JsonValueKind.String)
            {
                instrumentFile = legacy.GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(instrumentFile))
        {
            throw new InvalidOperationException("Instrument file must be specified in config for streaming mode.");
        }

        var path = ResolvePath(instrumentFile);
        var specs = InstrumentsCsvLoader.Load(path);
        var instruments = specs.Select(spec =>
        {
            var symbol = NormalizeInstrument(spec.Symbol);
            return new Instrument(new InstrumentId(symbol), symbol, spec.PriceDecimals, spec.PipSize);
        }).ToList();

        return new InMemoryInstrumentCatalog(instruments);
    }

    private static Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder> CreateBuilders(IEnumerable<Instrument> instruments, IEnumerable<BarInterval> intervals)
    {
        var map = new Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder>();
        foreach (var instrument in instruments)
        {
            foreach (var interval in intervals)
            {
                map[(instrument.Id, interval)] = new IntervalBarBuilder(interval);
            }
        }
        return map;
    }

    private RiskConfig? ResolveRiskConfig(JsonDocument raw)
    {
        if (raw.RootElement.TryGetProperty("risk", out var riskNode) && riskNode.ValueKind == JsonValueKind.Object)
        {
            try
            {
                return RiskConfigParser.Parse(riskNode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse risk config; defaulting to built-in config.");
            }
        }
        return null;
    }

    private static string ResolveRiskMode(JsonDocument raw)
    {
        if (raw.RootElement.TryGetProperty("featureFlags", out var featureNode) &&
            featureNode.ValueKind == JsonValueKind.Object &&
            featureNode.TryGetProperty("risk", out var riskNode) &&
            riskNode.ValueKind == JsonValueKind.String)
        {
            var mode = riskNode.GetString();
            if (!string.IsNullOrWhiteSpace(mode))
            {
                return mode.Trim().ToLowerInvariant();
            }
        }

        return "shadow";
    }

    private string? ComputeDataVersion(JsonDocument raw, string configDir)
    {
        try
        {
            if (!raw.RootElement.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var paths = new List<string>();
            if (dataNode.TryGetProperty("instrumentsFile", out var instNode) && instNode.ValueKind == JsonValueKind.String)
            {
                var value = instNode.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    paths.Add(ResolvePath(value));
                }
            }

            if (dataNode.TryGetProperty("ticks", out var ticksNode) && ticksNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in ticksNode.EnumerateObject())
                {
                    var value = entry.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        paths.Add(ResolvePath(value));
                    }
                }
            }

            if (paths.Count == 0)
            {
                return null;
            }

            return DataVersion.Compute(paths);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute data version snapshot.");
            return null;
        }
    }

    private string ResolvePath(string relativeOrAbsolute)
    {
        if (Path.IsPathRooted(relativeOrAbsolute))
        {
            return relativeOrAbsolute;
        }

        var root = _configDirectory ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(root, relativeOrAbsolute));
    }

    private static DateTime AlignToMinute(DateTime utc)
    {
        var ts = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0, DateTimeKind.Utc);
    }

    private static bool IsSentinelTimestamp(DateTime timestamp) => timestamp == TimestampSentinel;

    private static string NormalizeInstrument(string? raw)
    {
        var canonical = OandaInstrumentNormalizer.ToCanonical(raw);
        return string.IsNullOrWhiteSpace(canonical) ? string.Empty : canonical;
    }

    private static decimal ComputeMid(decimal bid, decimal ask)
    {
        if (bid > 0m && ask > 0m) return (bid + ask) / 2m;
        if (ask > 0m) return ask;
        if (bid > 0m) return bid;
        return 0m;
    }

    private IReadOnlyList<(string Label, BarInterval Interval)> ParseTimeframes(IEnumerable<string>? rawValues)
    {
        if (rawValues is null)
        {
            return Array.Empty<(string, BarInterval)>();
        }

        var results = new List<(string Label, BarInterval Interval)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rawValues)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var code = raw.Trim().ToUpperInvariant();
            if (!seen.Add(code))
            {
                continue;
            }

            var interval = code switch
            {
                "M15" => new BarInterval(TimeSpan.FromMinutes(15)),
                "M30" => new BarInterval(TimeSpan.FromMinutes(30)),
                "H1" => BarInterval.OneHour,
                "H4" => new BarInterval(TimeSpan.FromHours(4)),
                "D1" => BarInterval.OneDay,
                _ => default
            };

            if (interval.Duration == TimeSpan.Zero)
            {
                _logger.LogWarning("Unsupported timeframe '{Timeframe}' in configuration; skipping.", code);
                continue;
            }

            results.Add((code, interval));
        }

        return results;
    }

    private string ResolveSnapshotPath(string? configuredPath)
    {
        var baseDirectory = Path.GetDirectoryName(_configuration.ConfigPath);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.GetFullPath(Path.Combine(baseDirectory ?? Directory.GetCurrentDirectory(), configuredPath));
        }

        var root = string.IsNullOrWhiteSpace(baseDirectory) ? Directory.GetCurrentDirectory() : baseDirectory;
        return Path.Combine(root, "state", "engine-loop-snapshot.json");
    }

    private static string GenerateEngineInstanceId()
    {
        var guid = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        return $"loop-{guid[..12]}";
    }

    private string ResolveTimeframeLabel(TimeSpan duration)
    {
        if (_timeframeLabelByTicks.TryGetValue(duration.Ticks, out var label))
        {
            return label;
        }

        if (Math.Abs(duration.TotalMinutes - 15d) < 0.1d) return "M15";
        if (Math.Abs(duration.TotalMinutes - 30d) < 0.1d) return "M30";
        if (Math.Abs(duration.TotalHours - 1d) < 0.01d) return "H1";
        if (Math.Abs(duration.TotalHours - 4d) < 0.01d) return "H4";
        if (Math.Abs(duration.TotalDays - 1d) < 0.001d) return "D1";

        var minutes = Math.Round(duration.TotalMinutes);
        if (minutes > 0)
        {
            return $"{minutes:0}M";
        }

        return $"{duration.TotalSeconds:0}s";
    }

    private IReadOnlyList<NewsEvent> LoadNewsEvents(NewsBlackoutConfig? config)
    {
        if (config is null || !config.Enabled)
        {
            return Array.Empty<NewsEvent>();
        }

        var path = ResolveNewsSourcePath(config.SourcePath);

        try
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("News blackout enabled but schedule file missing path={Path}", path);
                return Array.Empty<NewsEvent>();
            }

            var json = File.ReadAllText(path);
            var events = JsonSerializer.Deserialize<List<NewsEvent>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (events is null || events.Count == 0)
            {
                return Array.Empty<NewsEvent>();
            }
            return events;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to load news blackout schedule from {Path}", path);
            return Array.Empty<NewsEvent>();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to load news blackout schedule from {Path}", path);
            return Array.Empty<NewsEvent>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to load news blackout schedule from {Path}", path);
            return Array.Empty<NewsEvent>();
        }
    }

    private async Task StartNewsFeedMonitorAsync(NewsBlackoutConfig? config)
    {
        if (_newsFeedRunner is not null)
        {
            await _newsFeedRunner.DisposeAsync().ConfigureAwait(false);
            _newsFeedRunner = null;
        }

        if (config is null || !config.Enabled)
        {
            return;
        }

        var path = ResolveNewsSourcePath(config.SourcePath);
        var feed = new FileNewsFeed(path, _logger);
        _newsFeedRunner = NewsFeedRunner.Start(
            feed,
            _state,
            events =>
            {
                var loop = _engineLoop;
                if (loop is null)
                {
                    _logger.LogDebug("News events dropped because engine loop is not available yet (batch size={Count}).", events.Count);
                    return;
                }
                loop.UpdateNewsEvents(events);
            },
            config,
            _logger,
            () => DateTime.UtcNow);
    }

    private string ResolveNewsSourcePath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return "/opt/tiyf/news-stub/today.json";
        }

        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var root = _configDirectory ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(root, configuredPath));
    }
}
