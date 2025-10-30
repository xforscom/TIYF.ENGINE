namespace TiYf.Engine.Host;

public sealed class EngineHostOptions
{
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
    public bool EnableMetrics { get; set; } = true;
    public bool EnableStreamingFeed { get; set; } = false;
    public TimeSpan StreamStaleThreshold { get; set; } = TimeSpan.FromSeconds(15);
    public int StreamAlertThreshold { get; set; } = 3;
}

