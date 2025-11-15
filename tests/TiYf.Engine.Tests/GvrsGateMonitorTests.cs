using System;
using System.Collections.Generic;
using TiYf.Engine.Core;
using TiYf.Engine.Sim;
using Xunit;

namespace TiYf.Engine.Tests;

public class GvrsGateMonitorTests
{
    private static GlobalVolatilityGateConfig LiveConfig(string? maxBucket = "moderate", decimal? maxEwma = null)
    {
        return new GlobalVolatilityGateConfig(
            EnabledMode: "live",
            EntryThreshold: 0m,
            EwmaAlpha: 0.3m,
            Components: Array.Empty<GlobalVolatilityComponentConfig>(),
            LiveMaxBucket: maxBucket,
            LiveMaxEwma: maxEwma);
    }

    [Fact]
    public void LiveGate_BlocksWhenBucketExceedsLimit()
    {
        var monitor = new GvrsGateMonitor(LiveConfig("moderate"), _ => { });
        var result = monitor.Evaluate("volatile", 0.9m, 0.8m, hasValue: true, "EURUSD", "H1", DateTime.UtcNow);

        Assert.NotNull(result);
        Assert.True(result!.Value.Blocked);
        Assert.Equal("ALERT_BLOCK_GVRS_GATE", result.Value.Alert.EventType);
    }

    [Fact]
    public void LiveGate_AllowsWithinBucket()
    {
        var monitor = new GvrsGateMonitor(LiveConfig("volatile"), _ => { });
        var result = monitor.Evaluate("moderate", 0.2m, 0.1m, hasValue: true, "EURUSD", "H1", DateTime.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void LiveGate_BlocksOnEwmaThreshold()
    {
        var monitor = new GvrsGateMonitor(LiveConfig(null, 0.4m), _ => { });
        var result = monitor.Evaluate("moderate", 0.8m, 0.5m, hasValue: true, "EURUSD", "H1", DateTime.UtcNow);

        Assert.NotNull(result);
        Assert.True(result!.Value.Blocked);
    }

    [Fact]
    public void ShadowMode_NoBlocking()
    {
        var shadowConfig = new GlobalVolatilityGateConfig(
            EnabledMode: "shadow",
            EntryThreshold: 0m,
            EwmaAlpha: 0.3m,
            Components: Array.Empty<GlobalVolatilityComponentConfig>());
        var monitor = new GvrsGateMonitor(shadowConfig, _ => { });
        var result = monitor.Evaluate("volatile", 0.9m, 0.9m, hasValue: true, "EURUSD", "H1", DateTime.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void LiveGate_IgnoresUnseededSnapshots()
    {
        var monitor = new GvrsGateMonitor(LiveConfig("moderate"), _ => { });
        var result = monitor.Evaluate("volatile", 0.9m, 0.8m, hasValue: false, "EURUSD", "H1", DateTime.UtcNow);

        Assert.Null(result);
    }
}
