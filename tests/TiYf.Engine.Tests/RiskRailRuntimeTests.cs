using System;
using System.Collections.Generic;
using System.Linq;
using TiYf.Engine.Core;
using TiYf.Engine.Sim;
using Xunit;

namespace TiYf.Engine.Tests;

public class RiskRailRuntimeTests
{
    private static readonly DateTime BaseTimestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SessionWindowBlocksOutsideConfiguredHours()
    {
        var config = new RiskConfig
        {
            SessionWindow = new SessionWindowConfig(TimeSpan.FromHours(7), TimeSpan.FromHours(22))
        };
        var gates = new List<(string Gate, bool Throttled)>();
        var runtime = new RiskRailRuntime(config, "hash", Array.Empty<NewsEvent>(), (gate, throttled) => gates.Add((gate, throttled)), 100_000m);

        var bar = new Bar(new InstrumentId("EURUSD"), BaseTimestamp.AddMinutes(-1), BaseTimestamp, 1.0m, 1.0m, 1.0m, 1.0m, 1m);
        runtime.UpdateBar(bar, null);

        var decisionTs = new DateTime(2025, 1, 1, 23, 10, 0, DateTimeKind.Utc);
        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", decisionTs, 100);

        Assert.False(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_BLOCK_SESSION_WINDOW");
        Assert.Contains(gates, g => g.Gate == "session_window" && !g.Throttled);
    }

    [Fact]
    public void DailyGainCapHalvesUnitsWhenThresholdExceeded()
    {
        var config = new RiskConfig
        {
            DailyCap = new DailyCapConfig(null, 50m, DailyCapAction.HalfSize)
        };
        var gates = new List<(string Gate, bool Throttled)>();
        var runtime = new RiskRailRuntime(config, "hash", Array.Empty<NewsEvent>(), (gate, throttled) => gates.Add((gate, throttled)), 100_000m);
        var tracker = new PositionTracker();

        var openTs = BaseTimestamp.AddMinutes(-30);
        tracker.OnFill(new ExecutionFill("D1-01", "EURUSD", TradeSide.Buy, 1.0000m, 1_000, openTs), "schema", "hash", "adapter", null);

        var bar = new Bar(new InstrumentId("EURUSD"), BaseTimestamp.AddMinutes(-1), BaseTimestamp, 1.06m, 1.06m, 1.06m, 1.06m, 1m);
        runtime.UpdateBar(bar, tracker);

        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", BaseTimestamp, 100);

        Assert.True(outcome.Allowed);
        Assert.Equal(50, outcome.Units);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_THROTTLE_DAILY_GAIN_CAP");
        Assert.Contains(gates, g => g.Gate == "daily_gain_cap" && g.Throttled);
    }

    [Fact]
    public void DailyLossCapBlocksWhenLossThresholdBreached()
    {
        var config = new RiskConfig
        {
            DailyCap = new DailyCapConfig(-100m, null, DailyCapAction.Block)
        };
        var gates = new List<(string Gate, bool Throttled)>();
        var runtime = new RiskRailRuntime(config, "hash", Array.Empty<NewsEvent>(), (gate, throttled) => gates.Add((gate, throttled)), 100_000m);
        var tracker = new PositionTracker();

        var openTs = BaseTimestamp.AddHours(-1);
        tracker.OnFill(new ExecutionFill("D2-01", "EURUSD", TradeSide.Buy, 1.0000m, 1_000, openTs), "schema", "hash", "adapter", null);
        tracker.OnFill(new ExecutionFill("D2-01", "EURUSD", TradeSide.Sell, 0.9000m, 1_000, openTs.AddMinutes(5)), "schema", "hash", "adapter", null);

        var bar = new Bar(new InstrumentId("EURUSD"), BaseTimestamp.AddMinutes(-1), BaseTimestamp, 0.9m, 0.9m, 0.9m, 0.9m, 1m);
        runtime.UpdateBar(bar, tracker);

        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", BaseTimestamp, 100);

        Assert.False(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_BLOCK_DAILY_LOSS_CAP");
        Assert.Contains(gates, g => g.Gate == "daily_loss_cap" && !g.Throttled);
    }

    [Fact]
    public void GlobalDrawdownBlocksNewEntries()
    {
        var config = new RiskConfig
        {
            GlobalDrawdown = new GlobalDrawdownConfig(-500m),
            MaxRunDrawdownCCY = 500m
        };
        var gates = new List<(string Gate, bool Throttled)>();
        var runtime = new RiskRailRuntime(config, "hash", Array.Empty<NewsEvent>(), (gate, throttled) => gates.Add((gate, throttled)), 100_000m);
        var tracker = new PositionTracker();

        var openTs = BaseTimestamp.AddHours(-2);
        tracker.OnFill(new ExecutionFill("D3-01", "EURUSD", TradeSide.Buy, 1.0000m, 1_000, openTs), "schema", "hash", "adapter", null);
        tracker.OnFill(new ExecutionFill("D3-01", "EURUSD", TradeSide.Sell, 0.4000m, 1_000, openTs.AddMinutes(10)), "schema", "hash", "adapter", null);

        var bar = new Bar(new InstrumentId("EURUSD"), BaseTimestamp.AddMinutes(-1), BaseTimestamp, 0.4m, 0.4m, 0.4m, 0.4m, 1m);
        runtime.UpdateBar(bar, tracker);

        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", BaseTimestamp, 100);

        Assert.False(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_BLOCK_GLOBAL_DRAWDOWN");
        Assert.Contains(gates, g => g.Gate == "global_drawdown" && !g.Throttled);
    }

    [Fact]
    public void NewsBlackoutBlocksWithinWindow()
    {
        var newsEvent = new NewsEvent(new DateTime(2025, 1, 1, 13, 30, 0, DateTimeKind.Utc), "high", new List<string> { "USD" });
        var config = new RiskConfig
        {
            NewsBlackout = new NewsBlackoutConfig(true, 30, 30, null, 60)
        };
        var gates = new List<(string Gate, bool Throttled)>();
        var runtime = new RiskRailRuntime(config, "hash", new[] { newsEvent }, (gate, throttled) => gates.Add((gate, throttled)), 100_000m);

        var bar = new Bar(new InstrumentId("EURUSD"), BaseTimestamp.AddMinutes(-1), BaseTimestamp, 1.0m, 1.0m, 1.0m, 1.0m, 1m);
        runtime.UpdateBar(bar, null);

        var decisionTs = new DateTime(2025, 1, 1, 13, 10, 0, DateTimeKind.Utc);
        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", decisionTs, 100);

        Assert.False(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_BLOCK_NEWS_BLACKOUT");
        Assert.Contains(gates, g => g.Gate == "news_blackout" && !g.Throttled);
    }

    [Fact]
    public void BrokerDailyLossCapEmitsSoftAlert()
    {
        var config = new RiskConfig
        {
            BrokerDailyLossCapCcy = 1_000m
        };
        var runtime = new RiskRailRuntime(config, "hash", Array.Empty<NewsEvent>(), null, 100_000m);
        var tracker = new PositionTracker();
        var openTs = BaseTimestamp.AddHours(-1);
        tracker.OnFill(new ExecutionFill("BDL-01", "EURUSD", TradeSide.Buy, 1.0000m, 5_000, openTs), "schema", "hash", "adapter", null);
        tracker.OnFill(new ExecutionFill("BDL-01", "EURUSD", TradeSide.Sell, 0.5000m, 5_000, openTs.AddMinutes(5)), "schema", "hash", "adapter", null);

        var bar = new Bar(new InstrumentId("EURUSD"), BaseTimestamp.AddMinutes(-1), BaseTimestamp, 0.5m, 0.5m, 0.5m, 0.5m, 1m);
        runtime.UpdateBar(bar, tracker);

        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", BaseTimestamp, 100);

        Assert.True(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_RISK_BROKER_DAILY_CAP_SOFT");
    }

    [Fact]
    public void MaxPositionUnitsEmitsSoftAlert()
    {
        var config = new RiskConfig
        {
            MaxPositionUnits = 200_000
        };
        var runtime = new RiskRailRuntime(config, "hash", Array.Empty<NewsEvent>(), null, 100_000m);
        var openPositions = new[]
        {
            new RiskPositionUnits("EURUSD", 180_000)
        };

        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", BaseTimestamp, 30_000, openPositions);

        Assert.True(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_RISK_MAX_POSITION_SOFT");
    }

    [Fact]
    public void SymbolUnitCapEmitsSoftAlert()
    {
        var config = new RiskConfig
        {
            SymbolUnitCaps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["EURUSD"] = 100_000
            }
        };
        var runtime = new RiskRailRuntime(config, "hash", Array.Empty<NewsEvent>(), null, 100_000m);
        var openPositions = new[]
        {
            new RiskPositionUnits("eurusd", 80_000)
        };

        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", BaseTimestamp, 30_000, openPositions);

        Assert.True(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_RISK_SYMBOL_CAP_SOFT");
    }

    [Fact]
    public void CooldownEmitsSoftAlertAfterLosses()
    {
        var cooldownConfig = new RiskCooldownConfig(true, 2, 30);
        RiskRailTelemetrySnapshot? lastTelemetry = null;
        var runtime = new RiskRailRuntime(
            new RiskConfig { Cooldown = cooldownConfig },
            "hash",
            Array.Empty<NewsEvent>(),
            null,
            100_000m,
            telemetryCallback: snapshot => lastTelemetry = snapshot,
            clock: () => BaseTimestamp);
        var tracker = new PositionTracker();

        var firstOpen = BaseTimestamp.AddMinutes(-60);
        tracker.OnFill(new ExecutionFill("CD-01", "EURUSD", TradeSide.Buy, 1.0000m, 1_000, firstOpen), "schema", "hash", "adapter", null);
        tracker.OnFill(new ExecutionFill("CD-01", "EURUSD", TradeSide.Sell, 0.5000m, 1_000, firstOpen.AddMinutes(5)), "schema", "hash", "adapter", null);
        runtime.UpdateBar(new Bar(new InstrumentId("EURUSD"), BaseTimestamp.AddMinutes(-2), BaseTimestamp.AddMinutes(-1), 0.5m, 0.5m, 0.5m, 0.5m, 1m), tracker);

        var secondOpen = BaseTimestamp.AddMinutes(-30);
        tracker.OnFill(new ExecutionFill("CD-02", "EURUSD", TradeSide.Buy, 1.0000m, 1_000, secondOpen), "schema", "hash", "adapter", null);
        tracker.OnFill(new ExecutionFill("CD-02", "EURUSD", TradeSide.Sell, 0.4000m, 1_000, secondOpen.AddMinutes(5)), "schema", "hash", "adapter", null);
        runtime.UpdateBar(new Bar(new InstrumentId("EURUSD"), BaseTimestamp.AddMinutes(-1), BaseTimestamp, 0.4m, 0.4m, 0.4m, 0.4m, 1m), tracker);

        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", BaseTimestamp, 100);

        Assert.True(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_RISK_COOLDOWN_SOFT");
        Assert.NotNull(lastTelemetry);
        Assert.True(lastTelemetry!.CooldownActive);
        Assert.Equal(1, lastTelemetry.CooldownTriggersTotal);
    }
}
