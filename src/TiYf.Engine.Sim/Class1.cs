using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TiYf.Engine.Core;
using TiYf.Engine.Core.Text;
using TiYf.Engine.Sidecar;
using TiYf.Engine.Core.Slippage;

namespace TiYf.Engine.Sim;

public sealed record GvrsGateConfig(bool Enabled = false);

public sealed record EngineConfig(
    string SchemaVersion,
    string RunId,
    [property: JsonPropertyName("config_id")] string ConfigId,
    string InstrumentFile,
    string InputTicksFile,
    string JournalRoot,
    string BarOutputEventType = "BAR_V1",
    string ClockMode = "sequence",
    string AdapterId = "stub",
    string BrokerId = "stub-sim",
    string AccountId = "account-stub",
    string? SlippageModel = "zero",
    SlippageProfile? Slippage = null,
    string[]? Instruments = null,
    string[]? Intervals = null,
    [property: JsonPropertyName("gvrs_gate")] GvrsGateConfig? GvrsGate = null
);

public static class EngineConfigLoader
{
    public static (EngineConfig config, string configHash, JsonDocument raw) Load(string path)
    {
        var rawBytes = File.ReadAllBytes(path);
        var hash = ConfigHash.Compute(rawBytes);
        var doc = JsonDocument.Parse(rawBytes);
        var cfg = doc.Deserialize<EngineConfig>() ?? throw new InvalidOperationException("Invalid config");
        if (string.IsNullOrWhiteSpace(cfg.ConfigId))
        {
            var root = doc.RootElement;
            var id = TryGetString(root, "config_id") ?? TryGetString(root, "config_version") ?? TryGetString(root, "configId");
            if (!string.IsNullOrWhiteSpace(id))
            {
                cfg = cfg with { ConfigId = id };
            }
            else
            {
                var fileName = Path.GetFileNameWithoutExtension(path);
                cfg = cfg with { ConfigId = string.IsNullOrWhiteSpace(fileName) ? "unknown" : fileName };
            }
        }
        return (cfg, hash, doc);
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        return el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}

public interface ITickSource : IEnumerable<PriceTick> { }

public sealed class CsvTickSource : ITickSource
{
    private readonly string _path;
    private readonly InstrumentId _instrumentId;
    private readonly List<PriceTick> _ordered;
    public CsvTickSource(string path, InstrumentId instrumentId)
    {
        _path = path; _instrumentId = instrumentId;
        _ordered = File.ReadLines(_path)
            .Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(line =>
            {
                var parts = line.Split(',');
                var raw = DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
                if (raw.Kind != DateTimeKind.Utc)
                    throw new InvalidDataException($"Tick timestamp must be UTC: {raw:o}");
                var ts = raw;
                var price = decimal.Parse(parts[1]);
                var vol = decimal.Parse(parts[2]);
                return new PriceTick(_instrumentId, ts, price, vol);
            })
            .OrderBy(t => t.UtcTimestamp)
            .ThenBy(t => t.InstrumentId.Value) // deterministic tie-breaker
            .ToList();
    }
    public IEnumerator<PriceTick> GetEnumerator() => _ordered.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class EngineLoop
{
    private readonly IClock _clock;
    private readonly Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder> _builders;
    private readonly IBarKeyTracker _barKeyTracker;
    private readonly IJournalWriter _journal;
    private readonly ITickSource _ticks;
    private ulong _seq;
    private readonly string _barEventType;
    private readonly Action<Bar>? _onBarEmitted;
    private readonly Action<int, int>? _onPositionMetrics;
    private readonly IRiskFormulas? _riskFormulas;
    private readonly IBasketRiskAggregator? _basketAggregator;
    private readonly IRiskEnforcer? _riskEnforcer; // new enforcement dependency
    private readonly List<PositionInitialRisk> _openPositions = new();
    private readonly string? _configHash; // pass-through for risk probe journaling
    private readonly string _schemaVersion;
    private readonly RiskConfig? _riskConfig;
    private readonly string _riskMode = "off"; // off|shadow|active
    private readonly decimal? _equityOverride;
    private readonly DeterministicScriptStrategy? _deterministicStrategy; // optional strategy for M0
    private readonly IExecutionAdapter? _execution;
    private readonly PositionTracker? _positions;
    private readonly TradesJournalWriter? _tradesWriter;
    private readonly string? _dataVersion;
    private readonly string _sourceAdapter;
    private readonly Dictionary<string, long> _openUnits = new(); // decisionId -> units
    private readonly SentimentGuardConfig? _sentimentConfig;
    private readonly Dictionary<string, int> _decisionCounters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _riskProbeCounters = new(StringComparer.Ordinal); // per-instrument deterministic counter for risk probe DecisionId
    private readonly Dictionary<string, Queue<decimal>> _sentimentWindows = new(StringComparer.Ordinal);
    private DateTime? _lastStrategyMinute; // to avoid emitting strategy actions multiple times per minute when multiple instrument ticks share the same minute
    private readonly bool _riskProbeEnabled = true; // feature flag
    private readonly IReadOnlyDictionary<long, string> _timeframeLabels;
    private readonly string _riskConfigHash;
    private readonly RiskRailRuntime? _riskRails;
    private readonly PromotionShadowRuntime? _promotionRuntime;
    private readonly MarketContextService? _marketContextService;
    private readonly GvrsShadowAlertManager? _gvrsAlertManager;
    private readonly Action<MarketContextService.GvrsSnapshot>? _onGvrsSnapshot;
    private readonly Action<PromotionShadowSnapshot>? _onPromotionShadow;
    private readonly ISlippageModel _slippageModel;
    private readonly Func<DateTime>? _utcNow;
    private readonly Dictionary<string, DateTime> _orderIdempotency = new(StringComparer.Ordinal);
    private readonly Queue<string> _orderIdQueue = new();
    private readonly Dictionary<string, DateTime> _cancelIdempotency = new(StringComparer.Ordinal);
    private readonly Queue<string> _cancelIdQueue = new();
    public const int OrderIdempotencyCapacity = 5000;
    public const int CancelIdempotencyCapacity = 5000;
    private readonly TimeSpan _idempotencyTtl = TimeSpan.FromHours(24);
    private DateTime _lastKillSwitchAlertUtc = DateTime.MinValue;
    private readonly Action<string, long>? _onOrderAccepted;
    private readonly Action? _onOrderRejected;
    private readonly Action<int, int, long>? _onIdempotencyMetrics;
    private readonly Action<string>? _warnCallback;
    private readonly Action<decimal>? _onSlippageApplied;
    private readonly IIdempotencyPersistence? _idempotencyPersistence;
    private long _idempotencyEvictions;
    private bool _orderEvictionWarned;
    private bool _cancelEvictionWarned;
    private readonly GvrsGateMonitor? _gvrsGateMonitor;

    private string? ExtractDataVersion() => _dataVersion;
    private DateTime UtcNow() => _utcNow?.Invoke() ?? DateTime.UtcNow;

    private static string BuildOrderKey(string decisionId, string symbol, string timeframe, DateTime minuteUtc)
        => $"{decisionId}|{symbol}|{timeframe}|{minuteUtc:O}";

    private bool IsDuplicateOrder(string key, DateTime now)
    {
        return _orderIdempotency.TryGetValue(key, out var ts) && now - ts < _idempotencyTtl;
    }

    private void RegisterOrderKey(string key, DateTime now)
    {
        _orderIdempotency[key] = now;
        _orderIdQueue.Enqueue(key);
        TrimOrderCache(now);
        ReportIdempotencyMetrics();
        _idempotencyPersistence?.AddKey(IdempotencyKind.Order, key, now);
    }

    private void UnregisterOrderKey(string key)
    {
        if (_orderIdempotency.Remove(key))
        {
            ReportIdempotencyMetrics();
            _idempotencyPersistence?.RemoveKey(IdempotencyKind.Order, key);
        }
    }

    private decimal ApplySlippage(decimal intentPrice, bool isBuy, string symbol, long units)
    {
        var adjusted = _slippageModel.Apply(intentPrice, isBuy, symbol, units, UtcNow());
        _onSlippageApplied?.Invoke(adjusted - intentPrice);
        return adjusted;
    }

    private bool IsDuplicateCancel(string key, DateTime now)
    {
        if (_cancelIdempotency.TryGetValue(key, out var ts) && now - ts < _idempotencyTtl)
        {
            return true;
        }

        _cancelIdempotency[key] = now;
        _cancelIdQueue.Enqueue(key);
        TrimCancelCache(now);
        ReportIdempotencyMetrics();
        _idempotencyPersistence?.AddKey(IdempotencyKind.Cancel, key, now);
        return false;
    }

    private void TrimOrderCache(DateTime now)
    {
        while (_orderIdQueue.Count > OrderIdempotencyCapacity)
        {
            var oldKey = _orderIdQueue.Dequeue();
            if (!_orderIdempotency.TryGetValue(oldKey, out var ts))
            {
                continue;
            }

            if (now - ts < _idempotencyTtl)
            {
                _idempotencyEvictions++;
                if (!_orderEvictionWarned)
                {
                    _warnCallback?.Invoke("Order idempotency cache eviction occurred while key was still within TTL (capacity exceeded).");
                    _orderEvictionWarned = true;
                }
            }

            _orderIdempotency.Remove(oldKey);
            _idempotencyPersistence?.RemoveKey(IdempotencyKind.Order, oldKey);
        }
    }

    private void TrimCancelCache(DateTime now)
    {
        while (_cancelIdQueue.Count > CancelIdempotencyCapacity)
        {
            var oldKey = _cancelIdQueue.Dequeue();
            if (!_cancelIdempotency.TryGetValue(oldKey, out var ts))
            {
                continue;
            }

            if (now - ts < _idempotencyTtl)
            {
                _idempotencyEvictions++;
                if (!_cancelEvictionWarned)
                {
                    _warnCallback?.Invoke("Cancel idempotency cache eviction occurred while key was still within TTL (capacity exceeded).");
                    _cancelEvictionWarned = true;
                }
            }

            _cancelIdempotency.Remove(oldKey);
            _idempotencyPersistence?.RemoveKey(IdempotencyKind.Cancel, oldKey);
        }
    }

    private void RestoreIdempotency(in IdempotencySnapshot snapshot)
    {
        foreach (var entry in snapshot.Orders.OrderBy(e => e.TimestampUtc))
        {
            _orderIdempotency[entry.Key] = entry.TimestampUtc;
            _orderIdQueue.Enqueue(entry.Key);
        }

        foreach (var entry in snapshot.Cancels.OrderBy(e => e.TimestampUtc))
        {
            _cancelIdempotency[entry.Key] = entry.TimestampUtc;
            _cancelIdQueue.Enqueue(entry.Key);
        }

        TrimOrderCache(snapshot.LoadedUtc);
        TrimCancelCache(snapshot.LoadedUtc);
        ReportIdempotencyMetrics();
    }

    private void ReportIdempotencyMetrics()
    {
        _onIdempotencyMetrics?.Invoke(_orderIdempotency.Count, _cancelIdempotency.Count, _idempotencyEvictions);
    }

    private bool TryValidateOrderSize(string symbol, long units, out long maxUnits)
    {
        maxUnits = 0;
        var caps = _riskConfig?.MaxUnitsPerSymbol;
        if (caps is null) return true;
        if (caps.TryGetValue(symbol, out var max) && units > max)
        {
            maxUnits = max;
            return false;
        }
        return true;
    }

    private static bool KillSwitchFlagged()
    {
        var kill = Environment.GetEnvironmentVariable("TIYF_KILLSWITCH");
        if (!string.IsNullOrWhiteSpace(kill) && (string.Equals(kill, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(kill, "true", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var path = Environment.GetEnvironmentVariable("TIYF_KILLSWITCH_FILE");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = "/opt/tiyf/kill.switch";
        }

        try
        {
            if (File.Exists(path))
            {
                return true;
            }
        }
        catch (IOException)
        {
            // ignore IO issues
        }
        catch (UnauthorizedAccessException)
        {
            // ignore permission issues
        }

        return false;
    }

    private enum ExecutionDispatchStatus
    {
        Accepted,
        Duplicate,
        BlockedKillSwitch,
        BlockedSizeLimit,
        Rejected,
        TransportError
    }

    private sealed record ExecutionDispatchOutcome(ExecutionDispatchStatus Status, ExecutionResult? Result);

    private static ExecutionFill EnsureFill(OrderRequest order, ExecutionResult result, decimal fallbackPrice)
    {
        if (result.Fill is { } fill)
        {
            return fill;
        }

        return new ExecutionFill(order.DecisionId, order.Symbol, order.Side, fallbackPrice, order.Units, order.UtcTs);
    }

    private async Task EmitKillSwitchAlertAsync(string symbol, string timeframe, DateTime decisionUtc, CancellationToken ct)
    {
        var now = UtcNow();
        if (_lastKillSwitchAlertUtc != DateTime.MinValue && now - _lastKillSwitchAlertUtc < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastKillSwitchAlertUtc = now;
        var payload = JsonSerializer.SerializeToElement(new
        {
            symbol,
            timeframe,
            ts = decisionUtc,
            reason = "kill_switch_active"
        });
        payload = EnrichWithGvrs(payload);
        await _journal.AppendAsync(new JournalEvent(++_seq, decisionUtc, "ALERT_KILLSWITCH", _sourceAdapter, payload), ct);
    }

    private async Task EmitSizeLimitAlertAsync(string symbol, string timeframe, long requestedUnits, long maxUnits, DateTime decisionUtc, decimal priceIntent, decimal priceSlipped, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            symbol,
            timeframe,
            ts = decisionUtc,
            requested_units = requestedUnits,
            max_units = maxUnits,
            price_intent = priceIntent,
            price_slipped = priceSlipped,
            reason = "size_limit"
        });
        payload = EnrichWithGvrs(payload);
        await _journal.AppendAsync(new JournalEvent(++_seq, decisionUtc, "ALERT_SIZE_LIMIT", _sourceAdapter, payload), ct);
    }

    private async Task EmitOrderRejectedAsync(OrderRequest order, string timeframe, ExecutionResult result, decimal priceIntent, decimal priceSlipped, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            decision_id = order.DecisionId,
            symbol = order.Symbol,
            timeframe,
            ts = order.UtcTs,
            side = order.Side.ToString(),
            units = order.Units,
            price_intent = priceIntent,
            price_slipped = priceSlipped,
            reason = string.IsNullOrWhiteSpace(result.Reason) ? "execution_rejected" : result.Reason,
            adapter_http_status = result.StatusCode,
            transient = result.Transient
        });
        payload = EnrichWithGvrs(payload);
        await _journal.AppendAsync(new JournalEvent(++_seq, order.UtcTs, "ALERT_ORDER_REJECTED", _sourceAdapter, payload), ct);
    }

    private async Task<ExecutionDispatchOutcome> DispatchOrderAsync(OrderRequest order, string timeframe, bool isExit, decimal priceIntent, decimal priceSlipped, CancellationToken ct)
    {
        if (_execution is null)
        {
            throw new InvalidOperationException("Execution adapter not configured.");
        }

        var now = UtcNow();
        var orderKey = BuildOrderKey(order.DecisionId, order.Symbol, timeframe, order.UtcTs);
        if (IsDuplicateOrder(orderKey, now))
        {
            Console.WriteLine($"OrderSend duplicate decision={order.DecisionId} symbol={order.Symbol} timeframe={timeframe}");
            return new ExecutionDispatchOutcome(ExecutionDispatchStatus.Duplicate, null);
        }

        if (!isExit && KillSwitchFlagged())
        {
            await EmitKillSwitchAlertAsync(order.Symbol, timeframe, order.UtcTs, ct);
            Console.WriteLine($"OrderSend blocked kill-switch decision={order.DecisionId} symbol={order.Symbol}");
            _gvrsAlertManager?.Clear(order.DecisionId);
            return new ExecutionDispatchOutcome(ExecutionDispatchStatus.BlockedKillSwitch, null);
        }

        if (!isExit && !TryValidateOrderSize(order.Symbol, order.Units, out var maxUnits))
        {
            await EmitSizeLimitAlertAsync(order.Symbol, timeframe, order.Units, maxUnits, order.UtcTs, priceIntent, priceSlipped, ct);
            Console.WriteLine($"OrderSend blocked size-limit decision={order.DecisionId} symbol={order.Symbol} units={order.Units} max={maxUnits}");
            _gvrsAlertManager?.Clear(order.DecisionId);
            return new ExecutionDispatchOutcome(ExecutionDispatchStatus.BlockedSizeLimit, null);
        }

        RegisterOrderKey(orderKey, now);

        var delays = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(750)
        };

        ExecutionResult? lastResult = null;
        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            if (attempt > 0)
            {
                var delay = delays[attempt];
                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

            try
            {
                lastResult = await _execution.ExecuteMarketAsync(order with { PriceIntent = priceSlipped }, ct);
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"OrderSend transport error attempt={attempt + 1} decision={order.DecisionId} symbol={order.Symbol} message={ex.Message}");
                lastResult = null;
                if (attempt == delays.Length - 1)
                {
                    UnregisterOrderKey(orderKey);
                    _onOrderRejected?.Invoke();
                    _gvrsAlertManager?.Clear(order.DecisionId);
                    return new ExecutionDispatchOutcome(ExecutionDispatchStatus.TransportError, null);
                }
                continue;
            }
            catch (Exception ex) // intentional last-resort to surface unknown adapter failures
            {
                Console.WriteLine($"OrderSend unexpected error attempt={attempt + 1} decision={order.DecisionId} symbol={order.Symbol} message={ex.Message}");
                UnregisterOrderKey(orderKey);
                _onOrderRejected?.Invoke();
                _gvrsAlertManager?.Clear(order.DecisionId);
                return new ExecutionDispatchOutcome(ExecutionDispatchStatus.TransportError, null);
            }

            if (lastResult.Accepted)
            {
                var acceptedUnits = lastResult.Fill?.Units ?? order.Units;
                _onOrderAccepted?.Invoke(order.Symbol, acceptedUnits);
                LogOrderSendOk(order.DecisionId, order.Symbol, lastResult.BrokerOrderId);
                return new ExecutionDispatchOutcome(ExecutionDispatchStatus.Accepted, lastResult);
            }

            Console.WriteLine($"OrderSend failure attempt={attempt + 1} decision={order.DecisionId} symbol={order.Symbol} reason={lastResult.Reason}");
            if (!lastResult.Transient || attempt == delays.Length - 1)
            {
                break;
            }
        }

        if (lastResult is not null)
        {
            UnregisterOrderKey(orderKey);
            await EmitOrderRejectedAsync(order, timeframe, lastResult, priceIntent, priceSlipped, ct);
            _onOrderRejected?.Invoke();
            _gvrsAlertManager?.Clear(order.DecisionId);
            return new ExecutionDispatchOutcome(ExecutionDispatchStatus.Rejected, lastResult);
        }

        _gvrsAlertManager?.Clear(order.DecisionId);
        UnregisterOrderKey(orderKey);
        _onOrderRejected?.Invoke();
        return new ExecutionDispatchOutcome(ExecutionDispatchStatus.TransportError, null);
    }

    private static void LogOrderSendOk(string decisionId, string symbol, string? brokerOrderId)
    {
        var id = string.IsNullOrWhiteSpace(brokerOrderId) ? "unknown" : brokerOrderId;
        Console.WriteLine($"OrderSend ok decision={decisionId} brokerOrderId={id} symbol={symbol}");
    }

    public EngineLoop(IClock clock, Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder> builders, IBarKeyTracker tracker, IJournalWriter journal, ITickSource ticks, string barEventType, Action<Bar>? onBarEmitted = null, Action<int, int>? onPositionMetrics = null, IRiskFormulas? riskFormulas = null, IBasketRiskAggregator? basketAggregator = null, string? configHash = null, string schemaVersion = TiYf.Engine.Core.Infrastructure.Schema.Version, IRiskEnforcer? riskEnforcer = null, RiskConfig? riskConfig = null, decimal? equityOverride = null, DeterministicScriptStrategy? deterministicStrategy = null, IExecutionAdapter? execution = null, PositionTracker? positions = null, TradesJournalWriter? tradesWriter = null, string? dataVersion = null, string sourceAdapter = "stub", long sizeUnitsFx = 1000, long sizeUnitsXau = 1, bool riskProbeEnabled = true, SentimentGuardConfig? sentimentConfig = null, string? penaltyConfig = null, bool forcePenalty = false, bool ciPenaltyScaffold = false, string riskMode = "off", string? riskConfigHash = null, IReadOnlyList<NewsEvent>? newsEvents = null, IReadOnlyDictionary<long, string>? timeframeLabels = null, Action<string, bool>? riskGateCallback = null, Action<RiskRailTelemetrySnapshot>? riskRailsTelemetryCallback = null, Action<PromotionShadowSnapshot>? promotionShadowCallback = null, Action<MarketContextService.GvrsSnapshot>? gvrsSnapshotCallback = null, Action<DateTime>? gvrsGateCallback = null, ISlippageModel? slippageModel = null, Func<DateTime>? utcNow = null, Action<string, long>? orderAcceptedCallback = null, Action? orderRejectedCallback = null, Action<int, int, long>? idempotencyMetricsCallback = null, Action<string>? warnCallback = null, Action<decimal>? slippageMetricsCallback = null, IIdempotencyPersistence? idempotencyPersistence = null, IdempotencySnapshot? persistedIdempotencySnapshot = null, BrokerCaps? brokerCaps = null)
    {
        _clock = clock; _builders = builders; _barKeyTracker = tracker; _journal = journal; _ticks = ticks; _barEventType = barEventType; _seq = (journal is FileJournalWriter fj ? fj.NextSequence : 1UL) - 1UL; _onBarEmitted = onBarEmitted; _onPositionMetrics = onPositionMetrics; _riskFormulas = riskFormulas; _basketAggregator = basketAggregator; _configHash = configHash; _schemaVersion = schemaVersion; _riskEnforcer = riskEnforcer; _riskConfig = riskConfig; _equityOverride = equityOverride; _deterministicStrategy = deterministicStrategy; _execution = execution; _positions = positions; _tradesWriter = tradesWriter; _dataVersion = dataVersion; _sourceAdapter = string.IsNullOrWhiteSpace(sourceAdapter) ? "stub" : sourceAdapter; _riskProbeEnabled = riskProbeEnabled; _sentimentConfig = sentimentConfig; _penaltyMode = penaltyConfig ?? "off"; _forcePenalty = forcePenalty; _ciPenaltyScaffold = ciPenaltyScaffold; _riskMode = string.IsNullOrWhiteSpace(riskMode) ? "off" : riskMode.ToLowerInvariant();
        _sizeUnitsFx = sizeUnitsFx; _sizeUnitsXau = sizeUnitsXau;
        _riskConfigHash = riskConfigHash ?? string.Empty;
        var gvrsConfig = riskConfig?.GlobalVolatilityGate ?? GlobalVolatilityGateConfig.Disabled;
        if (gvrsConfig.IsEnabled)
        {
            _marketContextService = new MarketContextService(gvrsConfig);
            _gvrsAlertManager = new GvrsShadowAlertManager();
        }
        _onGvrsSnapshot = gvrsSnapshotCallback;
        _slippageModel = slippageModel ?? new ZeroSlippageModel();
        _utcNow = utcNow;
        _onOrderAccepted = orderAcceptedCallback;
        _onOrderRejected = orderRejectedCallback;
        _onIdempotencyMetrics = idempotencyMetricsCallback;
        _warnCallback = warnCallback;
        _onSlippageApplied = slippageMetricsCallback;
        _idempotencyPersistence = idempotencyPersistence;
        var liveGateConfig = riskConfig?.GlobalVolatilityGate ?? GlobalVolatilityGateConfig.Disabled;
        _gvrsGateMonitor = liveGateConfig.LiveModeEnabled ? new GvrsGateMonitor(liveGateConfig, gvrsGateCallback) : null;
        if (persistedIdempotencySnapshot.HasValue)
        {
            RestoreIdempotency(persistedIdempotencySnapshot.Value);
        }
        ReportIdempotencyMetrics();
        if (_marketContextService is null)
        {
            _onGvrsSnapshot?.Invoke(new MarketContextService.GvrsSnapshot(0m, 0m, "unknown", gvrsConfig.EnabledMode, false));
        }
        _timeframeLabels = timeframeLabels ?? new Dictionary<long, string>();
        var startingEquity = equityOverride ?? 100_000m;
        _riskRails = riskConfig is not null
            ? new RiskRailRuntime(
                riskConfig,
                _riskConfigHash,
                newsEvents ?? Array.Empty<NewsEvent>(),
                riskGateCallback,
                startingEquity,
                telemetryCallback: riskRailsTelemetryCallback,
                clock: UtcNow,
                brokerCaps: brokerCaps)
            : null;
        _promotionRuntime = riskConfig?.Promotion is { Enabled: true } promo
            ? new PromotionShadowRuntime(promo)
            : null;
        _onPromotionShadow = promotionShadowCallback;
#if DEBUG
        if (_riskMode == "off" && riskConfig is not null && (riskConfig.EmitEvaluations || (riskConfig.MaxNetExposureBySymbol != null || riskConfig.MaxRunDrawdownCCY != null)))
        {
            throw new InvalidOperationException("DEBUG: riskMode resolved Off unexpectedly while riskConfig provided");
        }
#endif
    }

    public void UpdateNewsEvents(IReadOnlyList<NewsEvent> events)
    {
        _riskRails?.ReplaceNewsEvents(events ?? Array.Empty<NewsEvent>());
    }
    private readonly long _sizeUnitsFx; private readonly long _sizeUnitsXau;
    private readonly string _penaltyMode; private readonly bool _forcePenalty; private readonly bool _ciPenaltyScaffold;
    private bool _penaltyEmitted = false; // ensure single forced emission
                                          // Risk tracking (net exposure by symbol & run drawdown). Simplified placeholders until full logic filled in.
    private readonly Dictionary<string, decimal> _netExposureBySymbol = new(StringComparer.Ordinal);
    private bool _riskBlockCurrentBar = false; // reset per bar
    private int _riskEvalCount = 0; // counts INFO_RISK_EVAL emissions for test hooks
    private readonly Dictionary<string, int> _riskEvalCountBySymbol = new(StringComparer.Ordinal);

    private async Task TryEmitGvrsShadowAlert(GlobalVolatilityGateConfig config, string decisionId, string symbol, string timeframe, DateTime decisionUtc, CancellationToken ct)
    {
        if (_marketContextService is null || _gvrsAlertManager is null)
        {
            return;
        }

        var evaluation = _marketContextService.Evaluate(config);
        if (!_gvrsAlertManager.TryRegister(decisionId, evaluation.ShouldAlert))
        {
            return;
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            instrument = symbol,
            timeframe,
            ts = decisionUtc,
            gvrs_raw = evaluation.Raw,
            gvrs_ewma = evaluation.Ewma,
            gvrs_bucket = evaluation.Bucket,
            entry_threshold = config.EntryThreshold,
            mode = evaluation.Mode
        });

        await _journal.AppendAsync(new JournalEvent(++_seq, decisionUtc, "ALERT_SHADOW_GVRS_GATE", _sourceAdapter, payload), ct);
    }

    private JsonElement EnrichWithGvrs(JsonElement payload)
    {
        if (_marketContextService is null || !_marketContextService.HasValue)
        {
            return payload;
        }

        var node = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
        node["gvrs_raw"] = decimal.ToDouble(_marketContextService.CurrentRaw);
        node["gvrs_ewma"] = decimal.ToDouble(_marketContextService.CurrentEwma);
        var bucket = BucketNormalizer.Normalize(_marketContextService.CurrentBucket);
        if (bucket is not null)
        {
            node["gvrs_bucket"] = bucket;
        }

        var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private void ForceDrawdown(string symbol, decimal limit)
    {
        _riskRails?.ForceDrawdown(limit);
#if DEBUG
        if (_riskRails is not null)
        {
            Console.WriteLine($"DEBUG: ForceDrawdown(symbol={symbol}, limit={limit}, value={Math.Abs(_riskRails.CurrentDrawdown)})");
        }
        else
        {
            Console.WriteLine($"DEBUG: ForceDrawdown(symbol={symbol}, limit={limit})");
        }
#endif
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        foreach (var tick in _ticks)
        {
            // Advance the deterministic clock only when we encounter a new unique timestamp in the tick stream.
            // This prevents multi-instrument minutes from over-advancing the clock.
            if (_clock.UtcNow != tick.UtcTimestamp)
                _clock.Tick();

            // Deterministic ordering: order builders by instrument id then interval duration
            foreach (var kvp in _builders
                .Where(b => b.Key.Item1.Equals(tick.InstrumentId))
                .OrderBy(b => b.Key.Item1.Value, StringComparer.Ordinal)
                .ThenBy(b => b.Key.Item2.Duration.TotalSeconds))
            {
                var builder = kvp.Value;
                var maybe = builder.OnTick(tick);
                if (maybe is { } bar)
                {
                    var interval = kvp.Key.Item2;
                    var key = new BarKey(bar.InstrumentId, interval, bar.StartUtc);
                    if (_barKeyTracker.Seen(key)) continue; // idempotency guard
                    _barKeyTracker.Add(key);
                    // Canonical BAR_V1 emission (schema_version 1.2.0) - legacy BAR rows removed
                    var enriched = new
                    {
                        bar.InstrumentId,
                        IntervalSeconds = interval.Duration.TotalSeconds,
                        bar.StartUtc,
                        bar.EndUtc,
                        bar.Open,
                        bar.High,
                        bar.Low,
                        bar.Close,
                        bar.Volume
                    };
                    var json = JsonSerializer.SerializeToElement(enriched);
                    await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, _barEventType, _sourceAdapter, json), ct);

                    // Sentiment volatility guard (shadow or active) after bar emission, before strategy trade execution
                    bool barClamp = false; decimal? lastOriginalUnits = null; long? lastAdjustedUnits = null; string? lastAppliedSymbol = null; DateTime? lastAppliedTs = null;
                    if (_sentimentConfig is { Enabled: true, Mode: var modeVal } sg && (modeVal.Equals("shadow", StringComparison.OrdinalIgnoreCase) || modeVal.Equals("active", StringComparison.OrdinalIgnoreCase)))
                    {
                        var symbol = bar.InstrumentId.Value;
                        if (!_sentimentWindows.TryGetValue(symbol, out var q)) { q = new Queue<decimal>(sg.Window); _sentimentWindows[symbol] = q; }
                        var sample = SentimentVolatilityGuard.Compute(sg, symbol, bar.EndUtc, bar.Close, q, out _);
                        var zPayload = System.Text.Json.JsonSerializer.SerializeToElement(new { symbol = sample.Symbol, s_raw = sample.SRaw, z = sample.Z, sigma = sample.Sigma, ts = sample.Ts });
                        await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "INFO_SENTIMENT_Z_V1", _sourceAdapter, zPayload), ct);
                        if (sample.Clamped)
                        {
                            var clampPayload = System.Text.Json.JsonSerializer.SerializeToElement(new { symbol = sample.Symbol, reason = "volatility_guard", ts = sample.Ts });
                            await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "INFO_SENTIMENT_CLAMP_V1", _sourceAdapter, clampPayload), ct);
                            barClamp = true; lastAppliedSymbol = sample.Symbol; lastAppliedTs = sample.Ts;
                        }
                    }

                    _marketContextService?.OnBar(bar, interval);
                    if (_marketContextService is { HasValue: true } svc)
                    {
                        _onGvrsSnapshot?.Invoke(svc.Snapshot);
                    }
                    _riskRails?.UpdateBar(bar, _positions);
                    if (_promotionRuntime is not null)
                    {
                        var snapshot = _promotionRuntime.Evaluate(_positions, bar.EndUtc);
                        _onPromotionShadow?.Invoke(snapshot);
                    }

                    // === Risk evaluation & alerts (always before strategy scheduling) ===
                    _riskBlockCurrentBar = false; // reset
                    List<DeterministicScriptStrategy.ScheduledAction>? pendingActions = null;
                    // Pre-fetch pending actions once per minute for projection & later execution
                    var tickMinute = new DateTime(tick.UtcTimestamp.Year, tick.UtcTimestamp.Month, tick.UtcTimestamp.Day, tick.UtcTimestamp.Hour, tick.UtcTimestamp.Minute, 0, DateTimeKind.Utc);
                    bool newMinuteWindow = _lastStrategyMinute != tickMinute;
                    if (_deterministicStrategy is not null && newMinuteWindow)
                        pendingActions = _deterministicStrategy.Pending(tickMinute).ToList();
                    if (_riskMode != "off")
                    {
                        decimal netExposure = 0m;
                        foreach (var kv in _openUnits.OrderBy(k => k.Key, StringComparer.Ordinal))
                        {
                            decimal notionalPerUnit = bar.Close; // simple deterministic mapping
                            netExposure += kv.Value * notionalPerUnit;
                        }
                        // Project opens from pending actions before they execute (only if new minute window)
                        if (pendingActions is not null)
                        {
                            foreach (var act in pendingActions)
                            {
                                if (act.Side == Side.Close) continue; // closures reduce exposure but we ignore for conservative block decision
                                var baseUnits = act.Symbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase) ? _sizeUnitsXau : _sizeUnitsFx;
                                decimal notionalPerUnit = bar.Close;
                                netExposure += baseUnits * notionalPerUnit * (act.DecisionId.EndsWith("-01", StringComparison.Ordinal) ? 1 : -1);
                            }
                        }
                        _netExposureBySymbol[bar.InstrumentId.Value] = netExposure;
                        var maxRunDrawdown = _riskConfig?.MaxRunDrawdownCCY;
                        if (_riskConfig?.ForceDrawdownAfterEvals is { } forceMap && maxRunDrawdown.HasValue)
                        {
                            var symbol = bar.InstrumentId.Value;
                            if (!_riskEvalCountBySymbol.TryGetValue(symbol, out var perSymCount)) perSymCount = 0;
                            if (forceMap.TryGetValue(symbol, out var triggerN) && perSymCount + 1 == triggerN)
                            {
                                ForceDrawdown(symbol, maxRunDrawdown.Value);
                            }
                        }
                        decimal runDrawdown = _riskRails is not null ? Math.Abs(_riskRails.CurrentDrawdown) : 0m;
#if DEBUG
                        if (_riskMode != "off") Console.WriteLine($"DEBUG:RISK_EVAL symbol={bar.InstrumentId.Value} run_dd={runDrawdown} cap={_riskConfig?.MaxRunDrawdownCCY} evalCount={_riskEvalCount} perSym={(_riskEvalCountBySymbol.TryGetValue(bar.InstrumentId.Value, out var c) ? c : 0)}");
#endif
                        if (_riskConfig?.EmitEvaluations != false)
                        {
                            var evalPayload = JsonSerializer.SerializeToElement(new { symbol = bar.InstrumentId.Value, ts = bar.EndUtc, net_exposure = netExposure, run_drawdown = runDrawdown });
                            evalPayload = EnrichWithGvrs(evalPayload);
                            await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "INFO_RISK_EVAL_V1", _sourceAdapter, evalPayload), ct);
                            _riskEvalCount++;
                            var sym = bar.InstrumentId.Value; _riskEvalCountBySymbol[sym] = _riskEvalCountBySymbol.TryGetValue(sym, out var ec) ? ec + 1 : 1;
                        }
                        decimal lim = 0m;
                        bool exposureBreach = false;
                        if (_riskConfig?.MaxNetExposureBySymbol != null && _riskConfig.MaxNetExposureBySymbol.TryGetValue(bar.InstrumentId.Value, out var cap))
                        {
                            lim = cap;
                            // Treat breach as >= cap (not strictly >) so deterministic zero-cap promotion tests trigger an alert when projected exposure is zero or positive and cap=0.
                            exposureBreach = Math.Abs(netExposure) >= cap;
#if DEBUG
                            if (cap == 0) Console.WriteLine($"DEBUG:EXPOSURE_CHECK symbol={bar.InstrumentId.Value} net={netExposure} cap={cap} breach={exposureBreach} pending={(pendingActions?.Count ?? 0)} evalSeq={_seq + 1}");
#endif
                        }
                        bool drawdownBreach = false;
                        if (maxRunDrawdown.HasValue)
                        {
                            var limit = Math.Abs(maxRunDrawdown.Value);
                            drawdownBreach = runDrawdown > limit;
                        }
                        if (exposureBreach && _riskMode == "active")
                        {
                            var alertPayload = JsonSerializer.SerializeToElement(new { symbol = bar.InstrumentId.Value, ts = bar.EndUtc, limit = lim, value = netExposure, reason = "net_exposure_cap", config_hash = _riskConfigHash });
                            alertPayload = EnrichWithGvrs(alertPayload);
                            await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "ALERT_BLOCK_NET_EXPOSURE", _sourceAdapter, alertPayload), ct);
                            if (_riskMode == "active" && (_riskConfig?.BlockOnBreach ?? false)) _riskBlockCurrentBar = true;
                        }
                        if (drawdownBreach && maxRunDrawdown.HasValue && _riskMode == "active")
                        {
                            var limitValue = Math.Abs(maxRunDrawdown.Value);
                            var alertPayload = JsonSerializer.SerializeToElement(new { ts = bar.EndUtc, limit_ccy = limitValue, value_ccy = runDrawdown, reason = "drawdown_guard", config_hash = _riskConfigHash });
                            alertPayload = EnrichWithGvrs(alertPayload);
                            await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "ALERT_BLOCK_DRAWDOWN", _sourceAdapter, alertPayload), ct);
                            if (_riskMode == "active" && (_riskConfig?.BlockOnBreach ?? false)) _riskBlockCurrentBar = true;
                        }
                        // Shadow mode never blocks
                        if (_riskMode == "shadow" && _riskBlockCurrentBar) _riskBlockCurrentBar = false;
                    }
                    else
                    { // off mode: clear any previously fetched actions variable remains
                      // no risk events emitted
                    }

                    // === Strategy scheduling & execution (after risk eval) ===
                    if (_deterministicStrategy is not null && _execution is not null && _positions is not null)
                    {
                        if (newMinuteWindow && pendingActions is not null)
                        {
                            _lastStrategyMinute = tickMinute;
                            foreach (var act in pendingActions)
                            {
                                if (_riskBlockCurrentBar && _riskMode == "active") continue; // suppress trade placement on active breach
                                if (act.Side == Side.Close)
                                {
                                    var closeSide = act.DecisionId.EndsWith("-01", StringComparison.Ordinal) ? TradeSide.Sell : TradeSide.Buy;
                                    var units = _openUnits.TryGetValue(act.DecisionId, out var ou) ? ou : 0L;
                                    if (units <= 0)
                                    {
                                        continue;
                                    }

                                    var timeframeLabel = ResolveTimeframeLabel(interval);
                                    var intentPrice = bar.Close;
                                    var slippedPrice = ApplySlippage(intentPrice, closeSide == TradeSide.Buy, act.Symbol, units);
                                    var req = new OrderRequest(act.DecisionId, act.Symbol, closeSide, units, tickMinute, slippedPrice);
                                    var outcome = await DispatchOrderAsync(req, timeframeLabel, isExit: true, intentPrice, slippedPrice, ct);
                                    if (outcome.Status != ExecutionDispatchStatus.Accepted)
                                    {
                                        continue;
                                    }

                                    var executionResult = outcome.Result!;
                                    var fill = EnsureFill(req, executionResult, slippedPrice);
                                    if (_positions is not null)
                                    {
                                        _positions.OnFill(fill, _schemaVersion, _configHash ?? string.Empty, _sourceAdapter, ExtractDataVersion());
                                        if (_tradesWriter is not null)
                                        {
                                            foreach (var completed in _positions.Completed.Where(c => c.DecisionId == act.DecisionId))
                                            {
                                                _tradesWriter.Append(completed);
                                            }
                                        }

                                        var remaining = _positions.GetOpenUnits(act.DecisionId);
                                        if (remaining > 0)
                                        {
                                            _openUnits[act.DecisionId] = remaining;
                                        }
                                        else
                                        {
                                            _openUnits.Remove(act.DecisionId);
                                            _gvrsAlertManager?.Clear(act.DecisionId);
                                        }
                                    }
                                    else
                                    {
                                        var remaining = _openUnits.TryGetValue(act.DecisionId, out var existing)
                                            ? Math.Max(0, existing - fill.Units)
                                            : 0;
                                        if (remaining > 0)
                                        {
                                            _openUnits[act.DecisionId] = remaining;
                                        }
                                        else
                                        {
                                            _openUnits.Remove(act.DecisionId);
                                            _gvrsAlertManager?.Clear(act.DecisionId);
                                        }
                                    }
                                }
                                else
                                {
                                    bool firstLeg = act.DecisionId.EndsWith("-01", StringComparison.Ordinal);
                                    var side = firstLeg ? TradeSide.Buy : TradeSide.Sell;
                                    var baseUnits = act.Symbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase) ? _sizeUnitsXau : _sizeUnitsFx;
                                    long finalUnits = baseUnits;
                                    if (barClamp && _sentimentConfig is { Mode: var m } && m.Equals("active", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var scaled = (long)Math.Floor(baseUnits * 0.5m);
                                        if (baseUnits > 0 && scaled < 1) scaled = 1;
                                        finalUnits = scaled;
                                        lastOriginalUnits = baseUnits; lastAdjustedUnits = finalUnits;
                                    }
                                    var timeframeLabel = ResolveTimeframeLabel(interval);
                                    RiskRailOutcome? riskOutcome = null;
                                    if (_riskMode != "off" && firstLeg && _riskRails is not null)
                                    {
                                        IReadOnlyCollection<RiskPositionUnits>? openUnitsSnapshot = null;
                                        if (_positions is not null)
                                        {
                                            var snapshot = _positions.SnapshotOpenPositions();
                                            openUnitsSnapshot = snapshot.Count == 0
                                                ? Array.Empty<RiskPositionUnits>()
                                                : snapshot.Select(pos => new RiskPositionUnits(pos.Symbol, pos.Units)).ToArray();
                                        }
                                        riskOutcome = _riskRails.EvaluateNewEntry(act.Symbol, timeframeLabel, tickMinute, finalUnits, openUnitsSnapshot);
                                        foreach (var alert in riskOutcome.Alerts)
                                        {
                                            var payload = EnrichWithGvrs(alert.Payload);
                                            await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, alert.EventType, _sourceAdapter, payload), ct);
                                        }
                                        var gvrsConfig = _riskConfig?.GlobalVolatilityGate ?? GlobalVolatilityGateConfig.Disabled;
                                        await TryEmitGvrsShadowAlert(gvrsConfig, act.DecisionId, act.Symbol, timeframeLabel, tickMinute, ct);
                                        GvrsGateResult? gateResult = null;
                                        if (_gvrsGateMonitor is not null && _marketContextService is { } svcGate)
                                        {
                                            gateResult = _gvrsGateMonitor.Evaluate(
                                                svcGate.HasValue ? svcGate.CurrentBucket : null,
                                                svcGate.CurrentRaw,
                                                svcGate.CurrentEwma,
                                                svcGate.HasValue,
                                                act.Symbol,
                                                timeframeLabel,
                                                tickMinute);
                                            if (gateResult is { Alert: { } alert })
                                            {
                                                var gatePayload = EnrichWithGvrs(alert.Payload);
                                                await _journal.AppendAsync(new JournalEvent(++_seq, tickMinute, alert.EventType, _sourceAdapter, gatePayload), ct);
                                            }
                                        }
                                        if (gateResult?.Blocked == true)
                                        {
                                            continue;
                                        }
                                        if (_riskMode == "active")
                                        {
                                            if (!riskOutcome.Allowed)
                                            {
                                                _riskBlockCurrentBar = true;
                                                continue;
                                            }
                                            finalUnits = riskOutcome.Units;
                                        }
                                    }
                                    if (finalUnits <= 0)
                                    {
                                        continue;
                                    }
                                    var intentPrice = bar.Close;
                                    var slippedPrice = ApplySlippage(intentPrice, side == TradeSide.Buy, act.Symbol, finalUnits);
                                    var req = new OrderRequest(act.DecisionId, act.Symbol, side, finalUnits, tickMinute, slippedPrice);
                                    var outcome = await DispatchOrderAsync(req, timeframeLabel, isExit: false, intentPrice, slippedPrice, ct);
                                    if (outcome.Status != ExecutionDispatchStatus.Accepted)
                                    {
                                        continue;
                                    }

                                    var executionResult = outcome.Result!;
                                    var fill = EnsureFill(req, executionResult, slippedPrice);
                                    if (_positions is not null)
                                    {
                                        _positions.OnFill(fill, _schemaVersion, _configHash ?? string.Empty, _sourceAdapter, ExtractDataVersion());
                                        var remaining = _positions.GetOpenUnits(act.DecisionId);
                                        if (remaining > 0)
                                        {
                                            _openUnits[act.DecisionId] = remaining;
                                        }
                                        else
                                        {
                                            _openUnits.Remove(act.DecisionId);
                                        }
                                    }
                                    else
                                    {
                                        _openUnits[act.DecisionId] = fill.Units;
                                    }
                                    _gvrsAlertManager?.Clear(act.DecisionId);
                                }

                            }

                        }

                        // Emit APPLIED event if scaling occurred
                        if (lastOriginalUnits.HasValue && lastAdjustedUnits.HasValue && lastAppliedSymbol is not null && lastAppliedTs.HasValue && _sentimentConfig is { Mode: var modeApplied } && modeApplied.Equals("active", StringComparison.OrdinalIgnoreCase))
                        {
                            var appliedPayload = System.Text.Json.JsonSerializer.SerializeToElement(new { symbol = lastAppliedSymbol, ts = lastAppliedTs.Value, original_units = lastOriginalUnits.Value, adjusted_units = lastAdjustedUnits.Value, reason = "volatility_guard" });
                            await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "INFO_SENTIMENT_APPLIED_V1", _sourceAdapter, appliedPayload), ct);
                        }


                    }

                    // Deferred penalty emission path: if sentiment was enabled we emit penalty AFTER sentiment chain but before probe/trades (once only)
                    if (!_penaltyEmitted && _ciPenaltyScaffold && _forcePenalty && (_penaltyMode == "shadow" || _penaltyMode == "active"))
                    {
                        _penaltyEmitted = true;
                        var penSymbol = bar.InstrumentId.Value;
                        decimal orig = 200m; decimal adj = Math.Max(1, Math.Floor(orig * 0.5m));
                        var penaltyPayload = JsonSerializer.SerializeToElement(new { symbol = penSymbol, ts = bar.EndUtc, reason = "drawdown_guard", original_units = orig, adjusted_units = adj, penalty_scalar = (orig == 0 ? 0m : (adj / orig)) });
                        await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "PENALTY_APPLIED_V1", _sourceAdapter, penaltyPayload), ct);
                    }
                    // Emit risk probe (synthetic) if services configured
                    if (_riskFormulas is not null && _basketAggregator is not null && _riskProbeEnabled)
                    {
                        // Base synthetic values with configurable equity override
                        decimal equity = _equityOverride ?? 100_000m;
                        decimal notional = Math.Abs(bar.Close) * 1_000m / 100m; // simplistic mapping
                        decimal usedMarginAfter = notional / 20m; // assume 5% margin
                        decimal positionInitialRiskMoney = Math.Max(50m, bar.Volume * 10m);
                        var leverage = _riskFormulas.ProjectLeverage(notional, equity);
                        var marginPct = _riskFormulas.ProjectMarginUsagePct(usedMarginAfter, equity);
                        _openPositions.Add(new PositionInitialRisk(bar.InstrumentId, positionInitialRiskMoney, new Currency("USD")));
                        var basketPct = _basketAggregator.ComputeBasketRiskPct(_openPositions, BasketMode.Base, new Currency("USD"), equity);
                        // Canonical RISK_PROBE_V1 payload (PascalCase required fields) + extras retained
                        // Deterministic decision id for risk probe to ensure promotion determinism across runs
                        var rpSymbol = bar.InstrumentId.Value;
                        if (!_riskProbeCounters.TryGetValue(rpSymbol, out var rpCount)) rpCount = 0;
                        rpCount++;
                        _riskProbeCounters[rpSymbol] = rpCount;
                        var decisionId = $"RP-{rpSymbol}-{rpCount:D4}";
                        var riskProbe = new
                        {
                            // Required canonical fields for verifier
                            InstrumentId = bar.InstrumentId.Value,
                            ProjectedLeverage = leverage,
                            ProjectedMarginUsagePct = marginPct,
                            BasketRiskPct = basketPct,
                            // Extra diagnostic fields (snake_case preserved as legacy / auxiliary fields)
                            DecisionId = decisionId,
                            NotionalValue = notional,
                            Equity = equity,
                            UsedMarginAfterOrder = usedMarginAfter,
                            PositionInitialRiskMoney = positionInitialRiskMoney,
                            BasketMode = BasketMode.Base.ToString(),
                            SchemaVersion = _schemaVersion,
                            ConfigHash = _configHash ?? string.Empty
                        };
                        var probeJson = JsonSerializer.SerializeToElement(riskProbe);
                        probeJson = EnrichWithGvrs(probeJson);
                        await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "RISK_PROBE_V1", _sourceAdapter, probeJson), ct);

                        // Enforcement (if enforcer configured)
                        if (_riskEnforcer is not null)
                        {
                            var proposal = new Proposal(
                                InstrumentId: bar.InstrumentId.Value,
                                RequestedVolume: 1_000m, // placeholder mapping from price to volume
                                NotionalValue: notional,
                                UsedMarginAfterOrder: usedMarginAfter,
                                PositionInitialRiskMoney: positionInitialRiskMoney,
                                DecisionId: decisionId);
                            // Snapshot includes current open positions (include this bar's position risk)
                            var snapshotPositions = _openPositions.Select(o => (o.InstrumentId.Value, o.InitialRiskMoneyAccountCcy)).ToList();
                            var snapshot = new BasketSnapshot(snapshotPositions);
                            var riskCfg = _riskConfig ?? new RiskConfig();
                            var ctx = new RiskContext(equity, snapshot, riskCfg, new InMemoryInstrumentCatalog(new[] { new Instrument(new InstrumentId(bar.InstrumentId.Value), "SYM", 2, 0.0001m) }), new PassthroughFx());
                            var enforcement = _riskEnforcer.Enforce(proposal, ctx);
                            foreach (var alert in enforcement.Alerts)
                            {
                                var alertPayload = JsonSerializer.SerializeToElement(new
                                {
                                    alert.EventType,
                                    alert.DecisionId,
                                    alert.InstrumentId,
                                    alert.Reason,
                                    alert.Observed,
                                    alert.Cap,
                                    alert.Equity,
                                    alert.NotionalValue,
                                    alert.UsedMarginAfterOrder,
                                    alert.SchemaVersion,
                                    alert.ConfigHash
                                });
                                alertPayload = EnrichWithGvrs(alertPayload);
                                await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, alert.EventType, _sourceAdapter, alertPayload), ct);
                            }
                        }
                    }
                    _onBarEmitted?.Invoke(bar);
                    _onPositionMetrics?.Invoke(_positions?.OpenCount ?? 0, _openUnits.Count);
                }
            }
        }
    }

    private string ResolveTimeframeLabel(BarInterval interval)
    {
        if (_timeframeLabels.TryGetValue(interval.Duration.Ticks, out var label) && !string.IsNullOrWhiteSpace(label))
        {
            return label;
        }
        var duration = interval.Duration;
        if (duration.TotalHours >= 1 && Math.Abs(duration.TotalHours - Math.Round(duration.TotalHours)) < 1e-6)
        {
            return $"H{(int)Math.Round(duration.TotalHours)}";
        }
        if (duration.TotalMinutes >= 1 && Math.Abs(duration.TotalMinutes - Math.Round(duration.TotalMinutes)) < 1e-6)
        {
            return $"M{(int)Math.Round(duration.TotalMinutes)}";
        }
        return $"{duration.TotalMinutes:0}m";
    }
}
