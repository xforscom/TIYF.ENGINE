using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TiYf.Engine.Core;
using TiYf.Engine.Core.Instruments;
using TiYf.Engine.Sidecar;
using TiYf.Engine.Sim;

namespace TiYf.Engine.Host;

internal sealed class OandaStreamingService : BackgroundService
{
    private readonly EngineHostState _state;
    private readonly EngineHostConfiguration _configuration;
    private readonly OandaAdapterSettings _adapterSettings;
    private readonly OandaStreamSettings _streamSettings;
    private readonly ILogger<OandaStreamingService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EngineHostOptions _hostOptions;
    private readonly IConnectableExecutionAdapter? _executionAdapter;

    private LiveTickSource? _tickSource;
    private EngineLoop? _engineLoop;
    private HostJournalWriter? _journalWriter;
    private TradesJournalWriter? _tradesWriter;
    private PositionTracker? _positionTracker;
    private EngineConfig? _engineConfig;
    private string? _configDirectory;
    private string? _primaryInstrument;

    private Task? _engineTask;
    private Task? _heartbeatTask;

    private long _riskEvents;
    private long _alerts;
    private int _lastOpenPositions;
    private int _lastActiveOrders;
    private int _consecutiveFailures;
    private static readonly DateTime TimestampSentinel = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

    public OandaStreamingService(
        EngineHostState state,
        EngineHostConfiguration configuration,
        OandaAdapterSettings adapterSettings,
        OandaStreamSettings streamSettings,
        ILogger<OandaStreamingService> logger,
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
        _executionAdapter = executionAdapter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_streamSettings.Enable || !_hostOptions.EnableStreamingFeed)
        {
            _logger.LogInformation("OANDA streaming feed disabled (enable={Enable}, option={Option})", _streamSettings.Enable, _hostOptions.EnableStreamingFeed);
            return;
        }

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
            _state.UpdateStreamConnection(false);
        }
    }

    private Task InitializeRuntimeAsync(CancellationToken cancellationToken)
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

        var intervals = ResolveIntervals();
        var builders = CreateBuilders(instruments.All(), intervals);
        var tracker = new InMemoryBarKeyTracker();
        _tickSource = new LiveTickSource();
        _positionTracker = new PositionTracker();

        var clock = new SystemClock();
        var riskFormulas = new RiskFormulas();
        var basketAggregator = new BasketRiskAggregator();
        var riskConfig = ResolveRiskConfig(rawDoc);
        var riskMode = ResolveRiskMode(rawDoc);
        var dataVersion = ComputeDataVersion(rawDoc, _configDirectory);

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
            riskMode: riskMode);

        _logger.LogInformation("Streaming runtime initialized run_id={RunId} journal={Journal}", runId, journalWriter.RunDirectory);
        _state.SetLastLog($"stream:run_id={runId}");
        UpdateHostMetrics();
        return Task.CompletedTask;
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

        _tickSource?.Dispose();
        _tickSource = null;
        _engineLoop = null;
        _positionTracker = null;
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
        }
    }

    private void OnBarEmitted(Bar bar)
    {
        _state.SetLastDecision(bar.EndUtc);
        _state.UpdateLag(Math.Max(0d, (DateTime.UtcNow - bar.EndUtc).TotalMilliseconds));
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
        }
        if (eventType.StartsWith("INFO_RISK_", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _riskEvents);
        }
        UpdateHostMetrics();
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
            return new Instrument(new InstrumentId(symbol), symbol, spec.PriceDecimals);
        }).ToList();

        return new InMemoryInstrumentCatalog(instruments);
    }

    private static List<BarInterval> ResolveIntervals()
    {
        return new List<BarInterval>
        {
            BarInterval.OneHour,
            new BarInterval(TimeSpan.FromHours(4)),
            BarInterval.OneDay
        };
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
}
