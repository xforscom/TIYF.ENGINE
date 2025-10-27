using System.Text.Json;
using TiYf.Engine.Host;

namespace TiYf.Engine.Tests;

public class MetricsFormattingTests
{
    [Fact]
    public void Formatter_ProducesExpectedLines()
    {
        var state = new EngineHostState("oanda-demo", Array.Empty<string>());
        state.MarkConnected(true);
        state.UpdateLag(12.5);
        state.UpdatePendingOrders(1);
        state.SetMetrics(openPositions: 2, activeOrders: 1, riskEventsTotal: 3, alertsTotal: 4);

        var snapshot = state.CreateMetricsSnapshot();
        var metricsText = EngineMetricsFormatter.Format(snapshot);

        Assert.Contains("engine_heartbeat_age_seconds", metricsText);
        Assert.Contains("engine_bar_lag_ms", metricsText);
        Assert.Contains("engine_open_positions 2", metricsText);
        Assert.Contains("engine_alerts_total 4", metricsText);
    }

    [Fact]
    public void HealthPayload_IncludesMetrics()
    {
        var state = new EngineHostState("stub", new[] { "ff_a" });
        state.MarkConnected(true);
        state.SetMetrics(openPositions: 1, activeOrders: 0, riskEventsTotal: 5, alertsTotal: 6);
        var payload = state.CreateHealthPayload();
        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("heartbeat_age_seconds", out _));
        Assert.Equal(1, root.GetProperty("open_positions").GetInt32());
        Assert.Equal(5, root.GetProperty("risk_events_total").GetInt64());
        Assert.Equal(6, root.GetProperty("alerts_total").GetInt64());
    }
}
