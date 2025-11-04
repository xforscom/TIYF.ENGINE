using System.Globalization;
using System.Linq;
using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Host;

namespace TiYf.Engine.Tests;

public class MetricsFormattingTests
{
    [Fact]
    public void Formatter_ProducesExpectedLines()
    {
        var state = new EngineHostState("oanda-demo", Array.Empty<string>());
        state.MarkConnected(true);
        var loopStart = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var decisionTime = loopStart.AddHours(1);
        state.SetLoopStart(loopStart);
        state.SetTimeframes(new[] { "H1", "H4" });
        state.UpdateLag(12.5);
        state.UpdatePendingOrders(1);
        state.SetMetrics(openPositions: 2, activeOrders: 1, riskEventsTotal: 3, alertsTotal: 4);
        state.RecordStreamHeartbeat(DateTime.UtcNow);
        state.UpdateStreamConnection(true);
        state.RecordLoopDecision("H1", decisionTime);
        state.SetGvrsSnapshot(new MarketContextService.GvrsSnapshot(0.25m, 0.20m, "calm", "shadow", true));

        var snapshot = state.CreateMetricsSnapshot();
        var metricsText = EngineMetricsFormatter.Format(snapshot);

        Assert.Contains("engine_heartbeat_age_seconds", metricsText);
        Assert.Contains("engine_bar_lag_ms", metricsText);
        Assert.Contains("engine_open_positions 2", metricsText);
        Assert.Contains("engine_alerts_total 4", metricsText);
        Assert.Contains("engine_stream_connected 1", metricsText);
        Assert.Contains("engine_stream_heartbeat_age_seconds", metricsText);
        Assert.Contains("engine_loop_uptime_seconds", metricsText);
        Assert.Contains("engine_loop_iterations_total 1", metricsText);
        Assert.Contains("engine_decisions_total 1", metricsText);
        Assert.Contains("engine_loop_last_success_ts", metricsText);
        Assert.Contains("engine_gvrs_raw", metricsText);
        Assert.Contains("engine_gvrs_ewma", metricsText);
        Assert.Contains("engine_gvrs_bucket{bucket=\"Calm\"} 1", metricsText);
    }

    [Fact]
    public void HealthPayload_IncludesMetrics()
    {
        var state = new EngineHostState("stub", new[] { "ff_a" });
        state.MarkConnected(true);
        var loopStart = new DateTime(2024, 2, 2, 6, 0, 0, DateTimeKind.Utc);
        var decisionTime = loopStart.AddHours(4);
        state.SetLoopStart(loopStart);
        state.SetTimeframes(new[] { "H1", "H4" });
        state.SetMetrics(openPositions: 1, activeOrders: 0, riskEventsTotal: 5, alertsTotal: 6);
        state.RecordStreamHeartbeat(DateTime.UtcNow);
        state.UpdateStreamConnection(true);
        state.RecordLoopDecision("H4", decisionTime);
        state.SetGvrsSnapshot(new MarketContextService.GvrsSnapshot(0.15m, 0.10m, "moderate", "shadow", true));
        var payload = state.CreateHealthPayload();
        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("heartbeat_age_seconds", out _));
        Assert.Equal(1, root.GetProperty("open_positions").GetInt32());
        Assert.Equal(5, root.GetProperty("risk_events_total").GetInt64());
        Assert.Equal(6, root.GetProperty("alerts_total").GetInt64());
        Assert.Equal(1, root.GetProperty("stream_connected").GetInt32());
        Assert.True(root.TryGetProperty("stream_heartbeat_age_seconds", out _));
        var decisionString = root.GetProperty("last_decision_utc").GetString();
        Assert.False(string.IsNullOrWhiteSpace(decisionString));
        var parsedDecision = DateTime.Parse(
            decisionString,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        Assert.Equal(DateTime.SpecifyKind(decisionTime, DateTimeKind.Utc), parsedDecision);
        var timeframes = root.GetProperty("timeframes_active").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("H1", timeframes);
        Assert.Contains("H4", timeframes);
        Assert.True(root.TryGetProperty("gvrs_raw", out var gvrsRaw));
        Assert.Equal(0.15, gvrsRaw.GetDouble(), 3);
        Assert.Equal(0.10, root.GetProperty("gvrs_ewma").GetDouble(), 3);
        Assert.Equal("Moderate", root.GetProperty("gvrs_bucket").GetString());
    }
}
