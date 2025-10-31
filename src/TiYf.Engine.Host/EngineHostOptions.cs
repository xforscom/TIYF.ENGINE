namespace TiYf.Engine.Host;

public sealed class EngineHostOptions
{
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
    public bool EnableMetrics { get; set; } = true;
    public bool EnableStreamingFeed { get; set; } = false;
    public TimeSpan StreamStaleThreshold { get; set; } = TimeSpan.FromSeconds(15);
    public int StreamAlertThreshold { get; set; } = 3;
    public List<string> Timeframes { get; } = new() { "H1", "H4" };
    public double DecisionSkewToleranceMilliseconds { get; set; } = 120_000; // default 2 minutes
    public string? SnapshotPath { get; set; }
    public bool EnableContinuousLoop { get; set; } = true;
}

