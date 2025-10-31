using System.Collections.Generic;
using System.Linq;

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
            if (!_lastDecisionByTimeframe.ContainsKey(timeframe))
            {
                _lastDecisionByTimeframe[timeframe] = utc;
            }
            else
            {
                _lastDecisionByTimeframe[timeframe] = utc;
            }
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

    public void SetLastLog(string? message)
    {
        lock (_sync)
        {
            LastLog = message;
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
                loop_start_utc = _loopStartUtc
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
        return new EngineMetricsSnapshot(
            heartbeatAge,
            BarLagMilliseconds,
            PendingOrders,
            _openPositions,
            _activeOrders,
            _riskEventsTotal,
            _alertsTotal,
            streamConnected,
            streamHeartbeatAge,
            loopUptimeSeconds,
            _loopIterationsTotal,
            _decisionsTotal,
            loopLastSuccessUnix);
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
}


