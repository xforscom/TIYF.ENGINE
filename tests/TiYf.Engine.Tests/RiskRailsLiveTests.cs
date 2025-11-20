using System;
using System.Collections.Generic;
using TiYf.Engine.Core;
using TiYf.Engine.Sim;
using TiYf.Engine.Core.Infrastructure;
using Xunit;

namespace TiYf.Engine.Tests;

public class RiskRailsLiveTests
{
    private static RiskConfig LiveConfig(Action<RiskConfigBuilder> configure)
    {
        var builder = new RiskConfigBuilder();
        configure(builder);
        return builder.Build();
    }

    [Fact]
    public void SymbolCap_LiveMode_BlocksEntry()
    {
        var config = LiveConfig(b =>
        {
            b.SymbolCaps = new Dictionary<string, long> { { "EURUSD", 100_000 } };
            b.RiskRailsMode = "live";
        });
        var runtime = new RiskRailRuntime(config, "hash", Array.Empty<NewsEvent>(), gateCallback: null, startingEquity: 100_000m);
        var openPositions = new[] { new RiskPositionUnits("EURUSD", 90_000) };

        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", DateTime.UtcNow, 20_000, openPositions);

        Assert.False(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_RISK_SYMBOL_CAP_HARD");
    }

    [Fact]
    public void BrokerDailyLoss_LiveMode_BlocksEntry()
    {
        var config = LiveConfig(b =>
        {
            b.BrokerLossCap = 500m;
            b.RiskRailsMode = "live";
        });
        var runtime = new RiskRailRuntime(config, "hash", Array.Empty<NewsEvent>(), gateCallback: null, startingEquity: 100_000m);
        var tracker = new PositionTracker();
        tracker.OnFill(new ExecutionFill("T-001", "EURUSD", TradeSide.Buy, 1.20m, 10_000, DateTime.UtcNow.AddMinutes(-10)), Schema.Version, "hash", "test", null);
        var bar = new Bar(new InstrumentId("EURUSD"), DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow, 1.00m, 1.00m, 1.00m, 1.00m, 1m);
        runtime.UpdateBar(bar, tracker);

        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", DateTime.UtcNow, 1_000, Array.Empty<RiskPositionUnits>());

        Assert.False(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_RISK_BROKER_DAILY_CAP_HARD");
    }

    [Fact]
    public void TelemetryMode_DoesNotBlock()
    {
        var config = LiveConfig(b =>
        {
            b.SymbolCaps = new Dictionary<string, long> { { "EURUSD", 50_000 } };
            b.RiskRailsMode = "telemetry";
        });
        var runtime = new RiskRailRuntime(config, "hash", Array.Empty<NewsEvent>(), gateCallback: null, startingEquity: 100_000m);
        var openPositions = new[] { new RiskPositionUnits("EURUSD", 40_000) };

        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", DateTime.UtcNow, 20_000, openPositions);

        Assert.True(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_RISK_SYMBOL_CAP_SOFT");
    }

    private sealed class RiskConfigBuilder
    {
        public Dictionary<string, long>? SymbolCaps { get; set; }
        public decimal? BrokerLossCap { get; set; }
        public string RiskRailsMode { get; set; } = "telemetry";

        public RiskConfig Build()
        {
            return new RiskConfig
            {
                SymbolUnitCaps = SymbolCaps,
                BrokerDailyLossCapCcy = BrokerLossCap,
                Cooldown = RiskCooldownConfig.Disabled,
                RiskRailsMode = RiskRailsMode
            };
        }
    }
}
