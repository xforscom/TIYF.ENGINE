using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiYf.Engine.Core;
using TiYf.Engine.Core.Text;
using TiYf.Engine.Sim;

namespace TiYf.Engine.Host;

public sealed class EngineHostState
{
    private readonly object _sync = new();
    private readonly List<string> _featureFlags;
    private readonly Dictionary<string, DateTime?> _lastDecisionByTimeframe = new(StringComparer.OrdinalIgnoreCase);
    private string[] _timeframesActive = Array.Empty<string>();

    private int _openPositions;
    private int _activeOrders;
    private long _riskEventsTotal;
    private long _alertsTotal;
    private bool _streamConnected;
    private DateTime? _lastStreamHeartbeatUtc;

    private long _loopIterationsTotal;
    private long _decisionsTotal;
    private DateTime? _lastDecisionUtc;
    private DateTime? _loopLastSuccessUtc;
    private DateTime? _loopStartUtc;
    private string _riskConfigHash = string.Empty;
    private string _promotionConfigHash = string.Empty;
    private PromotionTelemetrySnapshot? _promotionTelemetry;
    private long _riskBlocksTotal;
    private long _riskThrottlesTotal;
    private readonly Dictionary<string, long> _riskBlocksByGate = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _riskThrottlesByGate = new(StringComparer.OrdinalIgnoreCase);
    private long _orderRejectsTotal;
    private readonly Dictionary<string, long> _lastOrderUnitsBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _idempotencyCacheSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["order"] = 0,
        ["cancel"] = 0
    };
    private long _idempotencyEvictionsTotal;
    private int _idempotencyPersistedLoaded;
    private int _idempotencyPersistedExpired;
    private DateTime? _idempotencyPersistenceLastLoadUtc;
    private string _slippageModel = "zero";
    private double? _slippageLastPriceDelta;
    private long _slippageAdjustedOrdersTotal;
    private double? _gvrsRaw;
    private double? _gvrsEwma;
    private string? _gvrsBucket;
    private long _reconciliationMismatchesTotal;
    private DateTime? _lastReconciliationUtc;
    private ReconciliationStatus _lastReconciliationStatus = ReconciliationStatus.Unknown;
    private DateTime? _newsFeedLastEventUtc;
    private long _newsFeedEventsFetchedTotal;
    private bool _newsBlackoutActive;
    private DateTime? _newsBlackoutWindowStart;
    private DateTime? _newsBlackoutWindowEnd;
    private string _configPath = string.Empty;
    private string _configHash = string.Empty;
    private readonly Dictionary<string, IReadOnlyCollection<string>> _secretProvenance = new(StringComparer.OrdinalIgnoreCase);
    private string _newsFeedSourceType = "file";
    private bool _gvrsGateBlockingEnabled;
    private bool _gvrsGateEnabled;
    private long _gvrsGateBlocksTotal;
    private DateTime? _gvrsGateLastBlockUtc;
    private decimal? _riskBrokerDailyLossCapCcy;
    private decimal _riskBrokerDailyLossUsedCcy;
    private long _riskBrokerDailyLossViolationsTotal;
    private long? _riskMaxPositionUnitsLimit;
    private long _riskMaxPositionUnitsUsed;
    private long _riskMaxPositionViolationsTotal;
    private IReadOnlyDictionary<string, long>? _riskSymbolCapLimits;
    private readonly Dictionary<string, long> _riskSymbolCapUsage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _riskSymbolCapViolations = new(StringComparer.OrdinalIgnoreCase);
    private bool _riskCooldownEnabled;
    private bool _riskCooldownActive;
    private DateTime? _riskCooldownActiveUntilUtc;
    private long _riskCooldownTriggersTotal;
    private int? _riskCooldownConsecutiveLosses;
    private int? _riskCooldownMinutes;

    public EngineHostState(string adapter, IEnumerable<string>? featureFlags)
    {
        Adapter = adapter;
        _featureFlags = featureFlags?.Select(f => f ?? string.Empty).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            ?? new List<string>();
        LastHeartbeatUtc = DateTime.UtcNow;
    }

    public string Adapter { get; }
    public bool Connected { get; private set; }
    public DateTime LastHeartbeatUtc { get; private set; }
    public DateTime? LastH1DecisionUtc { get; private set; }
    public double BarLagMilliseconds { get; private set; }
    public int PendingOrders { get; private set; }
    public string? LastLog { get; private set; }

    public IReadOnlyList<string> FeatureFlags
    {
        get
        {
            lock (_sync)
            {
                return _featureFlags.ToArray();
            }
        }
    }

    public IReadOnlyDictionary<string, DateTime?> LastDecisionsByTimeframe
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<string, DateTime?>(_lastDecisionByTimeframe, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void MarkConnected(bool value)
    {
        lock (_sync)
        {
            Connected = value;
            LastHeartbeatUtc = DateTime.UtcNow;
        }
    }

    public void Beat()
    {
        lock (_sync)
        {
            LastHeartbeatUtc = DateTime.UtcNow;
        }
    }

    public void SetLoopStart(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc)
        {
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        }
        lock (_sync)
        {
            _loopStartUtc = utc;
        }
    }

    public void SetTimeframes(IEnumerable<string> timeframes)
    {
        var frames = (timeframes ?? Array.Empty<string>())
            .Where(tf => !string.IsNullOrWhiteSpace(tf))
            .Select(tf => tf.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (_sync)
        {
            _timeframesActive = frames;
            foreach (var frame in frames)
            {
                if (!_lastDecisionByTimeframe.ContainsKey(frame))
                {
                    _lastDecisionByTimeframe[frame] = null;
                }
            }
        }
    }

    public void SetSlippageModel(string? model)
    {
        lock (_sync)
        {
            _slippageModel = string.IsNullOrWhiteSpace(model) ? "zero" : model.Trim().ToLowerInvariant();
        }
    }

    public void SetGvrsGateConfig(bool enabled, bool blockOnVolatile)
    {
        lock (_sync)
        {
            _gvrsGateEnabled = enabled;
            _gvrsGateBlockingEnabled = blockOnVolatile;
        }
    }

    public void RegisterGvrsGateBlock(DateTime utc)
    {
        lock (_sync)
        {
            _gvrsGateBlocksTotal++;
            _gvrsGateLastBlockUtc = NormalizeUtc(utc);
        }
    }

    public void RecordSlippage(decimal priceDelta)
    {
        lock (_sync)
        {
            // Always capture the last seen delta (including zero) so /health reflects the most recent order.
            _slippageLastPriceDelta = (double)priceDelta;
            if (priceDelta != 0m)
            {
                _slippageAdjustedOrdersTotal++;
            }
        }
    }

    public void UpdateNewsTelemetry(DateTime? lastEventUtc, long eventsFetchedTotal, bool blackoutActive, DateTime? windowStart, DateTime? windowEnd, string? sourceType = null)
    {
        lock (_sync)
        {
            // Normalize to UTC-only timestamps before surfacing in /health.
            _newsFeedLastEventUtc = NormalizeNullableUtc(lastEventUtc);
            _newsFeedEventsFetchedTotal = Math.Max(0, eventsFetchedTotal);
            _newsBlackoutActive = blackoutActive;
            _newsBlackoutWindowStart = NormalizeNullableUtc(windowStart);
            _newsBlackoutWindowEnd = NormalizeNullableUtc(windowEnd);
            if (!string.IsNullOrWhiteSpace(sourceType))
            {
                _newsFeedSourceType = NewsSourceTypeHelper.Normalize(sourceType);
            }
        }
    }

    public void UpdateIdempotencyMetrics(int orderCacheSize, int cancelCacheSize, long evictionsTotal)
    {
        lock (_sync)
        {
            _idempotencyCacheSizes["order"] = Math.Max(0, orderCacheSize);
            _idempotencyCacheSizes["cancel"] = Math.Max(0, cancelCacheSize);
            _idempotencyEvictionsTotal = Math.Max(0, evictionsTotal);
        }
    }

    public void SetIdempotencyPersistenceStats(int loaded, int expired, DateTime? lastLoadUtc)
    {
        lock (_sync)
        {
            _idempotencyPersistedLoaded = Math.Max(0, loaded);
            _idempotencyPersistedExpired = Math.Max(0, expired);
            _idempotencyPersistenceLastLoadUtc = NormalizeNullableUtc(lastLoadUtc);
        }
    }

    public void BootstrapLoopState(long decisionsTotal, long loopIterationsTotal, DateTime? lastDecisionUtc, IReadOnlyDictionary<string, DateTime?> decisionsByTimeframe)
    {
        lock (_sync)
        {
            _decisionsTotal = Math.Max(0, decisionsTotal);
            _loopIterationsTotal = Math.Max(0, loopIterationsTotal);
            _lastDecisionUtc = NormalizeNullableUtc(lastDecisionUtc);
            _loopLastSuccessUtc = _lastDecisionUtc;
            _lastDecisionByTimeframe.Clear();
            if (decisionsByTimeframe != null)
            {
                foreach (var kvp in decisionsByTimeframe)
                {
                    _lastDecisionByTimeframe[kvp.Key] = NormalizeNullableUtc(kvp.Value);
                    if (string.Equals(kvp.Key, "H1", StringComparison.OrdinalIgnoreCase))
                    {
                        LastH1DecisionUtc = NormalizeNullableUtc(kvp.Value);
                    }
                }
            }
            foreach (var frame in _timeframesActive)
            {
                if (!_lastDecisionByTimeframe.ContainsKey(frame))
                {
                    _lastDecisionByTimeframe[frame] = null;
                }
            }
        }
    }

    public void RecordLoopDecision(string timeframe, DateTime utc, bool incrementCounters = true)
    {
        utc = NormalizeUtc(utc);
        lock (_sync)
        {
            if (incrementCounters)
            {
                _decisionsTotal++;
                _loopIterationsTotal++;
            }
            _lastDecisionUtc = utc;
            _loopLastSuccessUtc = utc;
            _lastDecisionByTimeframe[timeframe] = utc;
            if (!_timeframesActive.Any(tf => string.Equals(tf, timeframe, StringComparison.OrdinalIgnoreCase)))
            {
                _timeframesActive = _timeframesActive.Concat(new[] { timeframe }).ToArray();
            }
            if (string.Equals(timeframe, "H1", StringComparison.OrdinalIgnoreCase))
            {
                LastH1DecisionUtc = utc;
            }
        }
    }

    public void SetLastDecision(DateTime utc)
    {
        RecordLoopDecision("H1", utc, incrementCounters: false);
    }

    public void UpdateLag(double milliseconds)
    {
        lock (_sync)
        {
            BarLagMilliseconds = milliseconds;
        }
    }

    public void UpdatePendingOrders(int count)
    {
        lock (_sync)
        {
            PendingOrders = count;
        }
    }

    public void RegisterOrderAccepted(string symbol, long units)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        lock (_sync)
        {
            _lastOrderUnitsBySymbol[symbol] = units;
        }
    }

    public void RegisterOrderRejected()
    {
        lock (_sync)
        {
            _orderRejectsTotal++;
        }
    }

    public void SetLastLog(string? message)
    {
        lock (_sync)
        {
            LastLog = message;
        }
    }

    public void SetGvrsSnapshot(in MarketContextService.GvrsSnapshot snapshot)
    {
        lock (_sync)
        {
            if (!snapshot.HasValue)
            {
                _gvrsRaw = null;
                _gvrsEwma = null;
                _gvrsBucket = null;
                return;
            }

            _gvrsRaw = (double)snapshot.Raw;
            _gvrsEwma = (double)snapshot.Ewma;
            _gvrsBucket = BucketNormalizer.Normalize(snapshot.Bucket);
        }
    }

    public void SetMetrics(int openPositions, int activeOrders, long riskEventsTotal, long alertsTotal)
    {
        lock (_sync)
        {
            _openPositions = Math.Max(0, openPositions);
            _activeOrders = Math.Max(0, activeOrders);
            _riskEventsTotal = Math.Max(0, riskEventsTotal);
            _alertsTotal = Math.Max(0, alertsTotal);
        }
    }

    public void RecordStreamHeartbeat(DateTime utcTimestamp)
    {
        utcTimestamp = NormalizeUtc(utcTimestamp);
        lock (_sync)
        {
            _lastStreamHeartbeatUtc = utcTimestamp;
        }
    }

    public void UpdateStreamConnection(bool connected)
    {
        lock (_sync)
        {
            _streamConnected = connected;
            if (!connected && _lastStreamHeartbeatUtc is null)
            {
                _lastStreamHeartbeatUtc = DateTime.UtcNow;
            }
        }
    }

    public void IncrementAlertCounter()
    {
        lock (_sync)
        {
            _alertsTotal++;
        }
    }

    public (long DecisionsTotal, long LoopIterationsTotal, DateTime? LastDecisionUtc, IReadOnlyDictionary<string, DateTime?> DecisionsByTimeframe) GetLoopSnapshotData()
    {
        lock (_sync)
        {
            return (
                _decisionsTotal,
                _loopIterationsTotal,
                _lastDecisionUtc,
                new Dictionary<string, DateTime?>(_lastDecisionByTimeframe, StringComparer.OrdinalIgnoreCase));
        }
    }

    public void SetRiskConfigHash(string hash)
    {
        lock (_sync)
        {
            _riskConfigHash = hash ?? string.Empty;
        }
    }

    public void SetConfigSource(string? path, string? hash)
    {
        lock (_sync)
        {
            _configPath = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
            _configHash = hash ?? string.Empty;
        }
    }

    public void UpdateSecretProvenance(IReadOnlyDictionary<string, IReadOnlyCollection<string>>? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        lock (_sync)
        {
            _secretProvenance.Clear();
            foreach (var kvp in snapshot)
            {
                var values = kvp.Value?.ToArray() ?? Array.Empty<string>();
                _secretProvenance[kvp.Key] = values;
            }
        }
    }


    public void SetPromotionConfig(PromotionConfig? promotion)
    {
        lock (_sync)
        {
            if (promotion is null || string.IsNullOrWhiteSpace(promotion.ConfigHash))
            {
                _promotionTelemetry = null;
                _promotionConfigHash = string.Empty;
                return;
            }

            var candidates = (promotion.ShadowCandidates ?? Array.Empty<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .ToArray();

            _promotionTelemetry = new PromotionTelemetrySnapshot(
                candidates,
                Math.Max(0, promotion.ProbationDays),
                Math.Max(0, promotion.MinTrades),
                ClampProbability(promotion.PromotionThreshold),
                ClampProbability(promotion.DemotionThreshold));
            _promotionConfigHash = promotion.ConfigHash ?? string.Empty;
        }
    }

    public void UpdateRiskRailsTelemetry(RiskRailTelemetrySnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        lock (_sync)
        {
            _riskBrokerDailyLossCapCcy = snapshot.BrokerDailyLossCapCcy;
            _riskBrokerDailyLossUsedCcy = snapshot.BrokerDailyLossUsedCcy;
            _riskBrokerDailyLossViolationsTotal = snapshot.BrokerDailyLossViolationsTotal;
            _riskMaxPositionUnitsLimit = snapshot.MaxPositionUnitsLimit;
            _riskMaxPositionUnitsUsed = snapshot.MaxPositionUnitsUsed;
            _riskMaxPositionViolationsTotal = snapshot.MaxPositionViolationsTotal;
            _riskSymbolCapLimits = snapshot.SymbolUnitCaps is null
                ? null
                : new Dictionary<string, long>(snapshot.SymbolUnitCaps, StringComparer.OrdinalIgnoreCase);
            _riskSymbolCapUsage.Clear();
            foreach (var usage in snapshot.SymbolUnitUsage)
            {
                _riskSymbolCapUsage[usage.Key] = usage.Value;
            }
            _riskSymbolCapViolations.Clear();
            foreach (var violation in snapshot.SymbolUnitViolations)
            {
                _riskSymbolCapViolations[violation.Key] = violation.Value;
            }
            _riskCooldownEnabled = snapshot.CooldownEnabled;
            _riskCooldownActive = snapshot.CooldownActive;
            _riskCooldownActiveUntilUtc = NormalizeNullableUtc(snapshot.CooldownActiveUntilUtc);
            _riskCooldownTriggersTotal = snapshot.CooldownTriggersTotal;
            _riskCooldownConsecutiveLosses = snapshot.CooldownConsecutiveLosses;
            _riskCooldownMinutes = snapshot.CooldownMinutes;
        }
    }

    public void RegisterRiskGateEvent(string gate, bool throttled)
    {
        lock (_sync)
        {
            if (throttled)
            {
                _riskThrottlesTotal++;
                if (!string.IsNullOrWhiteSpace(gate))
                {
                    _riskThrottlesByGate[gate] = _riskThrottlesByGate.TryGetValue(gate, out var existing) ? existing + 1 : 1;
                }
            }
            else
            {
                _riskBlocksTotal++;
                if (!string.IsNullOrWhiteSpace(gate))
                {
                    _riskBlocksByGate[gate] = _riskBlocksByGate.TryGetValue(gate, out var existing) ? existing + 1 : 1;
                }
            }
        }
    }

    public EngineMetricsSnapshot CreateMetricsSnapshot()
    {
        lock (_sync)
        {
            return CreateMetricsSnapshotUnsafe(DateTime.UtcNow);
        }
    }

    public object CreateHealthPayload()
    {
        var utcNow = DateTime.UtcNow;
        lock (_sync)
        {
            var metrics = CreateMetricsSnapshotUnsafe(utcNow);
            return new
            {
                adapter = Adapter,
                connected = Connected,
                last_h1_decision_utc = LastH1DecisionUtc,
                last_decision_utc = _lastDecisionUtc,
                bar_lag_ms = BarLagMilliseconds,
                pending_orders = PendingOrders,
                feature_flags = _featureFlags.ToArray(),
                last_heartbeat_utc = LastHeartbeatUtc,
                last_log = LastLog,
                heartbeat_age_seconds = metrics.HeartbeatAgeSeconds,
                open_positions = metrics.OpenPositions,
                active_orders = metrics.ActiveOrders,
                risk_events_total = metrics.RiskEventsTotal,
                alerts_total = metrics.AlertsTotal,
                stream_connected = metrics.StreamConnected,
                stream_heartbeat_age_seconds = metrics.StreamHeartbeatAgeSeconds,
                timeframes_active = _timeframesActive,
                last_decision_by_timeframe = new Dictionary<string, DateTime?>(_lastDecisionByTimeframe, StringComparer.OrdinalIgnoreCase),
                loop_iterations_total = metrics.LoopIterationsTotal,
                decisions_total = metrics.DecisionsTotal,
                loop_last_success_utc = _loopLastSuccessUtc,
                loop_start_utc = _loopStartUtc,
                config = new { path = _configPath, hash = _configHash },
                risk_config_hash = _riskConfigHash,
                promotion_config_hash = _promotionConfigHash,
                risk_blocks_total = _riskBlocksTotal,
                risk_throttles_total = _riskThrottlesTotal,
                risk_blocks_by_gate = new Dictionary<string, long>(_riskBlocksByGate, StringComparer.OrdinalIgnoreCase),
                risk_throttles_by_gate = new Dictionary<string, long>(_riskThrottlesByGate, StringComparer.OrdinalIgnoreCase),
                order_rejects_total = metrics.OrderRejectsTotal,
                last_order_size_units = new Dictionary<string, long>(metrics.LastOrderSizeBySymbol, StringComparer.OrdinalIgnoreCase),
                idempotency_cache_size = new Dictionary<string, long>(_idempotencyCacheSizes, StringComparer.OrdinalIgnoreCase),
                idempotency_evictions_total = _idempotencyEvictionsTotal,
                idempotency_persistence = CreateIdempotencyPersistenceHealth(),
                slippage_model = _slippageModel,
                slippage = CreateSlippageHealthUnsafe(),
                gvrs_raw = metrics.GvrsRaw,
                gvrs_ewma = metrics.GvrsEwma,
                gvrs_bucket = metrics.GvrsBucket,
                gvrs_gate = new
                {
                    bucket = metrics.GvrsBucket,
                    blocking_enabled = _gvrsGateBlockingEnabled,
                    last_block_utc = _gvrsGateLastBlockUtc
                },
                promotion = CreatePromotionHealthUnsafe(),
                news = CreateNewsHealthUnsafe(),
                risk_rails = CreateRiskRailsHealthUnsafe(),
                secrets = CreateSecretsHealthUnsafe(),
                reconciliation = CreateReconciliationHealthUnsafe()
            };
        }
    }

    private EngineMetricsSnapshot CreateMetricsSnapshotUnsafe(DateTime utcNow)
    {
        var heartbeatAge = Math.Max(0d, (utcNow - LastHeartbeatUtc).TotalSeconds);
        var streamHeartbeatUtc = _lastStreamHeartbeatUtc ?? LastHeartbeatUtc;
        var streamHeartbeatAge = Math.Max(0d, (utcNow - streamHeartbeatUtc).TotalSeconds);
        var streamConnected = _streamConnected ? 1 : 0;
        var loopUptimeSeconds = _loopStartUtc.HasValue ? Math.Max(0d, (utcNow - _loopStartUtc.Value).TotalSeconds) : 0d;
        var loopLastSuccessUnix = _loopLastSuccessUtc.HasValue ? new DateTimeOffset(_loopLastSuccessUtc.Value).ToUnixTimeSeconds() : 0d;
        var newsLastEventUnix = _newsFeedLastEventUtc.HasValue ? new DateTimeOffset(_newsFeedLastEventUtc.Value).ToUnixTimeSeconds() : (double?)null;
        var newsWindowsActive = _newsBlackoutActive ? 1 : 0;
        var newsSourceType = _newsFeedSourceType;
        var gvrsGateWouldBlock = _gvrsGateEnabled && _gvrsGateBlockingEnabled && string.Equals(_gvrsBucket, "volatile", StringComparison.OrdinalIgnoreCase);
        var gvrsGateLastBlockUnix = _gvrsGateLastBlockUtc.HasValue ? new DateTimeOffset(_gvrsGateLastBlockUtc.Value).ToUnixTimeSeconds() : (double?)null;
        var secretSnapshot = _secretProvenance.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyCollection<string>)(kvp.Value?.ToArray() ?? Array.Empty<string>()),
            StringComparer.OrdinalIgnoreCase);
        return new EngineMetricsSnapshot(
            heartbeatAge,
            BarLagMilliseconds,
            PendingOrders,
            _openPositions,
            _activeOrders,
            _riskEventsTotal,
            _alertsTotal,
            _orderRejectsTotal,
            streamConnected,
            streamHeartbeatAge,
            loopUptimeSeconds,
            _loopIterationsTotal,
            _decisionsTotal,
            loopLastSuccessUnix,
            _riskBlocksTotal,
            _riskThrottlesTotal,
            new Dictionary<string, long>(_riskBlocksByGate, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, long>(_riskThrottlesByGate, StringComparer.OrdinalIgnoreCase),
            _riskBrokerDailyLossCapCcy,
            _riskBrokerDailyLossUsedCcy,
            _riskBrokerDailyLossViolationsTotal,
            _riskMaxPositionUnitsLimit,
            _riskMaxPositionUnitsUsed,
            _riskMaxPositionViolationsTotal,
            new Dictionary<string, long>(_riskSymbolCapUsage, StringComparer.OrdinalIgnoreCase),
            _riskSymbolCapLimits is null ? null : new Dictionary<string, long>(_riskSymbolCapLimits, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, long>(_riskSymbolCapViolations, StringComparer.OrdinalIgnoreCase),
            _riskCooldownEnabled,
            _riskCooldownActive,
            _riskCooldownActiveUntilUtc.HasValue ? new DateTimeOffset(_riskCooldownActiveUntilUtc.Value).ToUnixTimeSeconds() : (double?)null,
            _riskCooldownTriggersTotal,
            _riskCooldownConsecutiveLosses,
            _riskCooldownMinutes,
            new Dictionary<string, long>(_lastOrderUnitsBySymbol, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, long>(_idempotencyCacheSizes, StringComparer.OrdinalIgnoreCase),
            _idempotencyEvictionsTotal,
            _slippageModel,
            _slippageLastPriceDelta,
            _slippageAdjustedOrdersTotal,
            newsLastEventUnix,
            _newsFeedEventsFetchedTotal,
            newsWindowsActive,
            newsSourceType,
            _gvrsRaw,
            _gvrsEwma,
            _gvrsBucket,
            _gvrsGateBlocksTotal,
            gvrsGateLastBlockUnix,
            _gvrsGateEnabled,
            _gvrsGateBlockingEnabled,
            gvrsGateWouldBlock,
            _configHash,
            _riskConfigHash,
            _promotionConfigHash,
            _promotionTelemetry,
            _reconciliationMismatchesTotal,
            _lastReconciliationUtc.HasValue ? new DateTimeOffset(_lastReconciliationUtc.Value).ToUnixTimeSeconds() : (double?)null,
            _lastReconciliationStatus.ToString().ToLowerInvariant(),
            _idempotencyPersistedLoaded,
            _idempotencyPersistedExpired,
            _idempotencyPersistenceLastLoadUtc.HasValue ? new DateTimeOffset(_idempotencyPersistenceLastLoadUtc.Value).ToUnixTimeSeconds() : (double?)null,
            secretSnapshot);
    }

    private static DateTime? NormalizeNullableUtc(DateTime? utc)
    {
        if (!utc.HasValue) return null;
        var value = utc.Value;
        return value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private static DateTime NormalizeUtc(DateTime utc)
    {
        return utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    }

    private object? CreatePromotionHealthUnsafe()
    {
        if (_promotionTelemetry is not { } promotion) return null;
        return new
        {
            candidates = promotion.Candidates.ToArray(),
            probation_days = promotion.ProbationDays,
            min_trades = promotion.MinTrades,
            promotion_threshold = promotion.PromotionThreshold,
            demotion_threshold = promotion.DemotionThreshold
        };
    }

    private object CreateSlippageHealthUnsafe()
    {
        return new
        {
            last_price_delta = _slippageLastPriceDelta,
            adjusted_orders_total = _slippageAdjustedOrdersTotal
        };
    }

    private object CreateNewsHealthUnsafe()
    {
        return new
        {
            last_event_utc = _newsFeedLastEventUtc,
            events_fetched_total = _newsFeedEventsFetchedTotal,
            blackout_active = _newsBlackoutActive,
            blackout_window_start = _newsBlackoutWindowStart,
            blackout_window_end = _newsBlackoutWindowEnd,
            source_type = _newsFeedSourceType
        };
    }

    private object CreateRiskRailsHealthUnsafe()
    {
        var symbolCaps = _riskSymbolCapLimits is null
            ? null
            : new Dictionary<string, long>(_riskSymbolCapLimits, StringComparer.OrdinalIgnoreCase);
        return new
        {
            broker_daily_cap_ccy = _riskBrokerDailyLossCapCcy,
            broker_daily_loss_used_ccy = _riskBrokerDailyLossUsedCcy,
            broker_daily_loss_violations_total = _riskBrokerDailyLossViolationsTotal,
            max_position_units = _riskMaxPositionUnitsLimit,
            max_position_units_used = _riskMaxPositionUnitsUsed,
            max_position_violations_total = _riskMaxPositionViolationsTotal,
            symbol_caps = symbolCaps,
            symbol_usage = new Dictionary<string, long>(_riskSymbolCapUsage, StringComparer.OrdinalIgnoreCase),
            symbol_violations = new Dictionary<string, long>(_riskSymbolCapViolations, StringComparer.OrdinalIgnoreCase),
            cooldown = new
            {
                enabled = _riskCooldownEnabled,
                active = _riskCooldownActive,
                active_until_utc = _riskCooldownActiveUntilUtc,
                triggers_total = _riskCooldownTriggersTotal,
                consecutive_losses = _riskCooldownConsecutiveLosses,
                minutes = _riskCooldownMinutes
            }
        };
    }

    private object CreateSecretsHealthUnsafe()
    {
        return _secretProvenance.ToDictionary(
            kvp => kvp.Key,
            kvp => string.Join(',', kvp.Value ?? Array.Empty<string>()),
            StringComparer.OrdinalIgnoreCase);
    }

    private object CreateIdempotencyPersistenceHealth()
    {
        return new
        {
            last_load_utc = _idempotencyPersistenceLastLoadUtc,
            loaded_keys = _idempotencyPersistedLoaded,
            expired_dropped = _idempotencyPersistedExpired
        };
    }

    private object? CreateReconciliationHealthUnsafe()
    {
        if (!_lastReconciliationUtc.HasValue && _reconciliationMismatchesTotal == 0 && _lastReconciliationStatus == ReconciliationStatus.Unknown)
        {
            return null;
        }

        return new
        {
            last_reconcile_utc = _lastReconciliationUtc,
            last_status = _lastReconciliationStatus.ToString().ToLowerInvariant(),
            mismatches_total = _reconciliationMismatchesTotal
        };
    }

    public void RecordReconciliationTelemetry(ReconciliationStatus status, long mismatchesDelta, DateTime utc)
    {
        lock (_sync)
        {
            _lastReconciliationUtc = NormalizeUtc(utc);
            _lastReconciliationStatus = status;
            if (mismatchesDelta > 0)
            {
                _reconciliationMismatchesTotal += mismatchesDelta;
            }
        }
    }

    private static decimal ClampProbability(decimal value)
    {
        if (value < 0m) return 0m;
        if (value > 1m) return 1m;
        return value;
    }

}
