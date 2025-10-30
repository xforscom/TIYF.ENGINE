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
    double StreamHeartbeatAgeSeconds);
