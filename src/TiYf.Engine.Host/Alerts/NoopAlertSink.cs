namespace TiYf.Engine.Host.Alerts;

public sealed class NoopAlertSink : IAlertSink
{
    public void Enqueue(AlertRecord alert)
    {
        // intentionally no-op
    }
}
