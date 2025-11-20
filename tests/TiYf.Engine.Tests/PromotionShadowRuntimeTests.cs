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
        var now = new DateTime(2025, 2, 1, 12, 0, 0, DateTimeKind.Utc);
        tracker.OnFill(new ExecutionFill("P1", "EURUSD", TradeSide.Buy, 1.0m, 10_000, now.AddHours(-3)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P1", "EURUSD", TradeSide.Sell, 1.1m, 10_000, now.AddHours(-2)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P2", "EURUSD", TradeSide.Buy, 1.0m, 10_000, now.AddHours(-2)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P2", "EURUSD", TradeSide.Sell, 1.2m, 10_000, now.AddHours(-1)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P3", "EURUSD", TradeSide.Buy, 1.0m, 10_000, now.AddHours(-1)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P3", "EURUSD", TradeSide.Sell, 0.9m, 10_000, now.AddMinutes(-30)), Schema.Version, "hash", "test", null);

        var snapshot = runtime.Evaluate(tracker, now);

        Assert.Equal(1, snapshot.PromotionsTotal);
        Assert.Equal(0, snapshot.DemotionsTotal);
        Assert.Equal(3, snapshot.TradeCount);
        Assert.Equal(2m / 3m, snapshot.WinRatio, 3);
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
        var now = new DateTime(2025, 2, 1, 12, 0, 0, DateTimeKind.Utc);
        tracker.OnFill(new ExecutionFill("P1", "EURUSD", TradeSide.Buy, 1.0m, 10_000, now.AddHours(-2)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P1", "EURUSD", TradeSide.Sell, 0.8m, 10_000, now.AddHours(-1)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P2", "EURUSD", TradeSide.Buy, 1.0m, 10_000, now.AddHours(-1)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("P2", "EURUSD", TradeSide.Sell, 0.9m, 10_000, now.AddMinutes(-30)), Schema.Version, "hash", "test", null);

        var snapshot = runtime.Evaluate(tracker, now);

        Assert.Equal(0, snapshot.PromotionsTotal);
        Assert.Equal(1, snapshot.DemotionsTotal);
    }

    [Fact]
    public void ShadowRuntime_Ignores_Trades_Outside_Probation_Window()
    {
        var now = new DateTime(2025, 2, 1, 12, 0, 0, DateTimeKind.Utc);
        var promo = new PromotionConfig(
            Enabled: true,
            ShadowCandidates: new[] { "A" },
            ProbationDays: 5,
            MinTrades: 2,
            PromotionThreshold: 0.6m,
            DemotionThreshold: 0.4m,
            ConfigHash: "hash");
        var runtime = new PromotionShadowRuntime(promo);
        var tracker = new PositionTracker();
        // Old trade (outside probation window)
        tracker.OnFill(new ExecutionFill("OLD", "EURUSD", TradeSide.Buy, 1.0m, 10_000, now.AddDays(-10)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("OLD", "EURUSD", TradeSide.Sell, 1.2m, 10_000, now.AddDays(-9)), Schema.Version, "hash", "test", null);
        // Recent trades (inside probation window)
        tracker.OnFill(new ExecutionFill("NEW1", "EURUSD", TradeSide.Buy, 1.0m, 10_000, now.AddDays(-2)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("NEW1", "EURUSD", TradeSide.Sell, 1.1m, 10_000, now.AddDays(-2).AddHours(1)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("NEW2", "EURUSD", TradeSide.Buy, 1.0m, 10_000, now.AddDays(-1)), Schema.Version, "hash", "test", null);
        tracker.OnFill(new ExecutionFill("NEW2", "EURUSD", TradeSide.Sell, 1.2m, 10_000, now.AddDays(-1).AddHours(1)), Schema.Version, "hash", "test", null);

        var snapshot = runtime.Evaluate(tracker, now);

        Assert.Equal(2, snapshot.TradeCount); // only recent trades considered
        Assert.Equal(1, snapshot.PromotionsTotal); // promotion triggered on recent wins
        Assert.Equal(0, snapshot.DemotionsTotal);
        Assert.Equal(1.0m, snapshot.WinRatio);
    }
}
