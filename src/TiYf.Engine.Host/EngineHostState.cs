namespace TiYf.Engine.Host;

public sealed class EngineHostState
{
    private readonly object _sync = new();
    private readonly List<string> _featureFlags;

    private int _openPositions;
    private int _activeOrders;
    private long _riskEventsTotal;
    private long _alertsTotal;

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

    public void SetLastDecision(DateTime utc)
    {
        lock (_sync)
        {
            LastH1DecisionUtc = utc;
        }
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
                bar_lag_ms = BarLagMilliseconds,
                pending_orders = PendingOrders,
                feature_flags = _featureFlags.ToArray(),
                last_heartbeat_utc = LastHeartbeatUtc,
                last_log = LastLog,
                heartbeat_age_seconds = metrics.HeartbeatAgeSeconds,
                open_positions = metrics.OpenPositions,
                active_orders = metrics.ActiveOrders,
                risk_events_total = metrics.RiskEventsTotal,
                alerts_total = metrics.AlertsTotal
            };
        }
    }

    private EngineMetricsSnapshot CreateMetricsSnapshotUnsafe(DateTime utcNow)
    {
        var heartbeatAge = Math.Max(0d, (utcNow - LastHeartbeatUtc).TotalSeconds);
        return new EngineMetricsSnapshot(
            heartbeatAge,
            BarLagMilliseconds,
            PendingOrders,
            _openPositions,
            _activeOrders,
            _riskEventsTotal,
            _alertsTotal);
    }
}
