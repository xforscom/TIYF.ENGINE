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
        state.RegisterOrderAccepted("EURUSD", 250);
        state.RegisterOrderRejected();
        state.UpdateIdempotencyMetrics(2, 1, 3);
        state.SetIdempotencyPersistenceStats(4, 1, DateTime.UtcNow);
        state.SetSlippageModel("fixed_bps");
        state.RecordSlippage(0.0002m);
        var lastNews = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        state.UpdateNewsTelemetry(lastNews, 3, true, lastNews.AddMinutes(-15), lastNews.AddMinutes(15));
        state.RecordReconciliationTelemetry(ReconciliationStatus.Match, 0, DateTime.UtcNow);

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
        Assert.Contains("engine_order_rejects_total 1", metricsText);
        Assert.Contains("engine_last_order_size_units{instrument=\"EURUSD\"} 250", metricsText);
        Assert.Contains("engine_idempotency_cache_size 3", metricsText);
        Assert.Contains("engine_idempotency_cache_size{kind=\"order\"} 2", metricsText);
        Assert.Contains("engine_idempotency_cache_size{kind=\"cancel\"} 1", metricsText);
        Assert.Contains("engine_idempotency_evictions_total 3", metricsText);
        Assert.Contains("engine_idempotency_persisted_loaded 4", metricsText);
        Assert.Contains("engine_idempotency_persisted_expired_total 1", metricsText);
        Assert.Contains("engine_slippage_model{model=\"fixed_bps\"} 1", metricsText);
        Assert.Contains("engine_slippage_last_price_delta 0.0002", metricsText);
        Assert.Contains("engine_slippage_adjusted_orders_total 1", metricsText);
        Assert.Contains("engine_news_events_fetched_total 3", metricsText);
        Assert.Contains("engine_news_blackout_windows_total 1", metricsText);
        Assert.Contains("engine_news_last_event_ts", metricsText);
        Assert.Contains("engine_reconcile_mismatches_total", metricsText);
        Assert.Contains("engine_reconcile_last_status{status=\"match\"} 1", metricsText);
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
        state.RegisterOrderAccepted("GBPUSD", 75);
        state.RegisterOrderRejected();
        state.UpdateIdempotencyMetrics(5, 4, 7);
        state.SetIdempotencyPersistenceStats(2, 1, DateTime.UtcNow);
        state.SetSlippageModel("fixed_bps");
        state.RecordSlippage(-0.00015m);
        var newsNow = new DateTime(2025, 2, 2, 6, 30, 0, DateTimeKind.Utc);
        state.UpdateNewsTelemetry(newsNow, 5, false, null, null);
        state.RecordReconciliationTelemetry(ReconciliationStatus.Mismatch, 2, DateTime.UtcNow);
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
        Assert.Equal(1, root.GetProperty("order_rejects_total").GetInt64());
        var lastOrderSizes = root.GetProperty("last_order_size_units");
        Assert.Equal(7, root.GetProperty("idempotency_evictions_total").GetInt64());
        var cacheSize = root.GetProperty("idempotency_cache_size");
        Assert.Equal(5, cacheSize.GetProperty("order").GetInt64());
        Assert.Equal(4, cacheSize.GetProperty("cancel").GetInt64());
        Assert.Equal("fixed_bps", root.GetProperty("slippage_model").GetString());
        var slippage = root.GetProperty("slippage");
        Assert.Equal(-0.00015, slippage.GetProperty("last_price_delta").GetDouble(), 6);
        Assert.Equal(1, slippage.GetProperty("adjusted_orders_total").GetInt64());
        var news = root.GetProperty("news");
        Assert.Equal(5, news.GetProperty("events_fetched_total").GetInt64());
        Assert.False(news.GetProperty("blackout_active").GetBoolean());
        Assert.Equal(75, lastOrderSizes.GetProperty("GBPUSD").GetInt64());
        var reconciliation = root.GetProperty("reconciliation");
        Assert.Equal(2, reconciliation.GetProperty("mismatches_total").GetInt64());
        Assert.Equal("mismatch", reconciliation.GetProperty("last_status").GetString());
        var persistence = root.GetProperty("idempotency_persistence");
        Assert.Equal(2, persistence.GetProperty("loaded_keys").GetInt32());
        Assert.Equal(1, persistence.GetProperty("expired_dropped").GetInt32());
    }
}
