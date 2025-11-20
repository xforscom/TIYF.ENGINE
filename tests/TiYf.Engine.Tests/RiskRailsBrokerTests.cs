using System;
using System.Collections.Generic;
using System.Reflection;
using TiYf.Engine.Core;
using TiYf.Engine.Sim;
using Xunit;

namespace TiYf.Engine.Tests;

public class RiskRailsBrokerTests
{
    private static readonly DateTime DecisionUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BrokerGuardrail_Blocks_WhenDailyLossCapExceeded()
    {
        var config = new RiskConfig { RiskRailsMode = "live" };
        var runtime = new RiskRailRuntime(
            config,
            "hash",
            Array.Empty<NewsEvent>(),
            gateCallback: null,
            startingEquity: 100_000m,
            brokerCaps: new BrokerCaps(100m, null, null),
            clock: () => DecisionUtc);
        SetDailyLoss(runtime, -150m);

        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", DecisionUtc, 10_000);

        Assert.False(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_RISK_BROKER_CAP_HARD");
    }

    [Fact]
    public void BrokerGuardrail_Blocks_WhenGlobalUnitsExceeded()
    {
        var config = new RiskConfig { RiskRailsMode = "live" };
        var runtime = new RiskRailRuntime(
            config,
            "hash",
            Array.Empty<NewsEvent>(),
            gateCallback: null,
            startingEquity: 100_000m,
            brokerCaps: new BrokerCaps(null, 1_000, null),
            clock: () => DecisionUtc);
        var openPositions = new[] { new RiskPositionUnits("EURUSD", 900) };

        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", DecisionUtc, 200, openPositions);

        Assert.False(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_RISK_BROKER_CAP_HARD");
    }

    [Fact]
    public void BrokerGuardrail_Blocks_WhenSymbolCapExceeded()
    {
        var config = new RiskConfig { RiskRailsMode = "live" };
        var caps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["EURUSD"] = 500
        };
        var runtime = new RiskRailRuntime(
            config,
            "hash",
            Array.Empty<NewsEvent>(),
            gateCallback: null,
            startingEquity: 100_000m,
            brokerCaps: new BrokerCaps(null, null, caps),
            clock: () => DecisionUtc);
        var openPositions = new[] { new RiskPositionUnits("eurusd", 400) };

        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", DecisionUtc, 200, openPositions);

        Assert.False(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_RISK_BROKER_CAP_HARD");
    }

    [Fact]
    public void BrokerGuardrail_TelemetryMode_DoesNotBlock()
    {
        RiskRailTelemetrySnapshot? telemetry = null;
        var config = new RiskConfig { RiskRailsMode = "telemetry" };
        var runtime = new RiskRailRuntime(
            config,
            "hash",
            Array.Empty<NewsEvent>(),
            gateCallback: null,
            startingEquity: 100_000m,
            telemetryCallback: snapshot => telemetry = snapshot,
            brokerCaps: new BrokerCaps(null, 1_000, null),
            clock: () => DecisionUtc);
        var openPositions = new[] { new RiskPositionUnits("EURUSD", 900) };

        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", DecisionUtc, 200, openPositions);

        Assert.True(outcome.Allowed);
        Assert.Contains(outcome.Alerts, a => a.EventType == "ALERT_RISK_BROKER_CAP_SOFT");
        Assert.NotNull(telemetry);
        Assert.True((telemetry?.BrokerCapBlocksTotal ?? 0) > 0);
    }

    [Fact]
    public void BrokerGuardrail_DoesNotRunForExits()
    {
        var config = new RiskConfig { RiskRailsMode = "live" };
        var runtime = new RiskRailRuntime(
            config,
            "hash",
            Array.Empty<NewsEvent>(),
            gateCallback: null,
            startingEquity: 100_000m,
            brokerCaps: new BrokerCaps(null, 1_000, null),
            clock: () => DecisionUtc);
        var outcome = runtime.EvaluateNewEntry("EURUSD", "H1", DecisionUtc, -500);

        Assert.True(outcome.Allowed);
        Assert.Empty(outcome.Alerts);
    }

    private static void SetDailyLoss(RiskRailRuntime runtime, decimal realized, decimal unrealized = 0m)
    {
        runtime.SetDailyPnlForTest(realized, unrealized);
    }
}
