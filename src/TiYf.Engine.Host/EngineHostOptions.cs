namespace TiYf.Engine.Host;

public sealed class EngineHostOptions
{
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
}

