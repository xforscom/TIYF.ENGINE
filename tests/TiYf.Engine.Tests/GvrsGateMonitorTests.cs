using System;
using System.Text.Json;
using TiYf.Engine.Sim;
using Xunit;

namespace TiYf.Engine.Tests;

public class GvrsGateMonitorTests
{
    [Fact]
    public void EmitsAlert_WhenVolatileBucket()
    {
        DateTime? recordedUtc = null;
        var monitor = new GvrsGateMonitor(enabled: true, onBlock: utc => recordedUtc = utc);
        var decisionUtc = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        var alert = monitor.TryCreateAlert("volatile", 0.8m, 0.7m, "EURUSD", "H1", decisionUtc);

        Assert.NotNull(alert);
        Assert.Equal("ALERT_BLOCK_GVRS_GATE", alert!.EventType);
        Assert.True(recordedUtc.HasValue);
        using var doc = JsonDocument.Parse(alert.Payload.GetRawText());
        var root = doc.RootElement;
        Assert.Equal("EURUSD", root.GetProperty("instrument").GetString());
        Assert.True(root.GetProperty("blocking_enabled").GetBoolean());
    }

    [Fact]
    public void SkipsAlert_WhenBucketNotVolatile()
    {
        var monitor = new GvrsGateMonitor(enabled: true, onBlock: _ => throw new InvalidOperationException("Should not be called"));
        var alert = monitor.TryCreateAlert("moderate", 0.1m, 0.2m, "GBPUSD", "H1", DateTime.UtcNow);

        Assert.Null(alert);
    }
}
