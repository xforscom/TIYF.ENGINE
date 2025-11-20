using System;
using System.Collections.Generic;
using TiYf.Engine.Core;
using TiYf.Engine.Sim;
using TiYf.Engine.Core.Infrastructure;
using Xunit;

namespace TiYf.Engine.Tests;

public class PromotionShadowRuntimeTests
{
    [Fact]
    public void ShadowRuntime_Promotes_OnWinRatio()
    {
        var promo = new PromotionConfig(
            Enabled: true,
            ShadowCandidates: new[] { "A", "B" },
            ProbationDays: 30,
            MinTrades: 3,
            PromotionThreshold: 0.6m,
            DemotionThreshold: 0.4m,
            ConfigHash: "hash");
        var runtime = new PromotionShadowRuntime(promo);
        var tracker = new PositionTracker();
        tracker.OnFill(new ExecutionFill("P1", "EURUSD", TradeSide.Buy, 1.0m, 10_000, DateTime.UtcNow.AddHours(-3)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P1", "EURUSD", TradeSide.Sell, 1.1m, 10_000, DateTime.UtcNow.AddHours(-2)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P2", "EURUSD", TradeSide.Buy, 1.0m, 10_000, DateTime.UtcNow.AddHours(-2)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P2", "EURUSD", TradeSide.Sell, 1.2m, 10_000, DateTime.UtcNow.AddHours(-1)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P3", "EURUSD", TradeSide.Buy, 1.0m, 10_000, DateTime.UtcNow.AddHours(-1)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P3", "EURUSD", TradeSide.Sell, 0.9m, 10_000, DateTime.UtcNow.AddMinutes(-30)), Schema.Version, "hash", "test", null);

        var snapshot = runtime.Evaluate(tracker);

        Assert.Equal(1, snapshot.PromotionsTotal);
        Assert.Equal(0, snapshot.DemotionsTotal);
        Assert.Equal(3, snapshot.TradeCount);
        Assert.True(snapshot.WinRatio >= 0.66m);
    }

    [Fact]
    public void ShadowRuntime_Demotes_OnLowWinRatio()
    {
        var promo = new PromotionConfig(
            Enabled: true,
            ShadowCandidates: new[] { "A" },
            ProbationDays: 30,
            MinTrades: 2,
            PromotionThreshold: 0.6m,
            DemotionThreshold: 0.4m,
            ConfigHash: "hash");
        var runtime = new PromotionShadowRuntime(promo);
        var tracker = new PositionTracker();
        tracker.OnFill(new ExecutionFill("P1", "EURUSD", TradeSide.Buy, 1.0m, 10_000, DateTime.UtcNow.AddHours(-2)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P1", "EURUSD", TradeSide.Sell, 0.8m, 10_000, DateTime.UtcNow.AddHours(-1)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P2", "EURUSD", TradeSide.Buy, 1.0m, 10_000, DateTime.UtcNow.AddHours(-1)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P2", "EURUSD", TradeSide.Sell, 0.9m, 10_000, DateTime.UtcNow.AddMinutes(-30)), Schema.Version, "hash", "test", null);

        var snapshot = runtime.Evaluate(tracker);

        Assert.Equal(0, snapshot.PromotionsTotal);
        Assert.Equal(1, snapshot.DemotionsTotal);
    }
}
