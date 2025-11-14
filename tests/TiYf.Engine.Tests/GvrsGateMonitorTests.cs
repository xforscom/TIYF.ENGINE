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
        var monitor = new GvrsGateMonitor(enabled: true, blockOnVolatile: true, onBlock: utc => recordedUtc = utc);
        var decisionUtc = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        var result = monitor.Evaluate("volatile", 0.8m, 0.7m, "EURUSD", "H1", decisionUtc);

        Assert.True(result.HasValue);
        var evaluation = result.Value;
        Assert.True(evaluation.Blocked);
        Assert.Equal("ALERT_BLOCK_GVRS_GATE", evaluation.Alert.EventType);
        Assert.True(recordedUtc.HasValue);
        using var doc = JsonDocument.Parse(result.Value.Alert.Payload.GetRawText());
        var root = doc.RootElement;
        Assert.Equal("EURUSD", root.GetProperty("instrument").GetString());
        Assert.True(root.GetProperty("blocking_enabled").GetBoolean());
    }

    [Fact]
    public void SkipsAlert_WhenBucketNotVolatile()
    {
        var monitor = new GvrsGateMonitor(enabled: true, blockOnVolatile: true, onBlock: _ => throw new InvalidOperationException("Should not be called"));
        var alert = monitor.Evaluate("moderate", 0.1m, 0.2m, "GBPUSD", "H1", DateTime.UtcNow);

        Assert.Null(alert);
    }

    [Fact]
    public void TelemetryOnly_DoesNotBlock()
    {
        var monitor = new GvrsGateMonitor(enabled: true, blockOnVolatile: false, onBlock: _ => { });
        var decisionUtc = new DateTime(2025, 2, 2, 12, 0, 0, DateTimeKind.Utc);

        var result = monitor.Evaluate("volatile", 0.4m, 0.3m, "GBPUSD", "H1", decisionUtc);

        Assert.True(result.HasValue);
        var evaluation = result.Value;
        Assert.False(evaluation.Blocked);
        using var doc = JsonDocument.Parse(evaluation.Alert.Payload.GetRawText());
        Assert.False(doc.RootElement.GetProperty("blocking_enabled").GetBoolean());
    }
}
