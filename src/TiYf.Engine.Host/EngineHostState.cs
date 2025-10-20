namespace TiYf.Engine.Host;

public sealed class EngineHostState
{
    private readonly object _sync = new();
    private readonly List<string> _featureFlags;

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

    public object CreateHealthPayload()
    {
        lock (_sync)
        {
            return new
            {
                adapter = Adapter,
                connected = Connected,
                last_h1_decision_utc = LastH1DecisionUtc,
                bar_lag_ms = BarLagMilliseconds,
                pending_orders = PendingOrders,
                feature_flags = _featureFlags.ToArray(),
                last_heartbeat_utc = LastHeartbeatUtc,
                last_log = LastLog
            };
        }
    }
}
