namespace TiYf.Engine.Host;

public readonly record struct EngineMetricsSnapshot(
    double HeartbeatAgeSeconds,
    double BarLagMilliseconds,
    int PendingOrders,
    int OpenPositions,
    int ActiveOrders,
    long RiskEventsTotal,
    long AlertsTotal,
    int StreamConnected,
    double StreamHeartbeatAgeSeconds,
    double LoopUptimeSeconds,
    long LoopIterationsTotal,
    long DecisionsTotal,
    double LoopLastSuccessUnixSeconds,
    long RiskBlocksTotal,
    long RiskThrottlesTotal,
    IReadOnlyDictionary<string, long> RiskBlocksByGate,
    IReadOnlyDictionary<string, long> RiskThrottlesByGate,
    double? GvrsRaw,
    double? GvrsEwma,
    string? GvrsBucket);
