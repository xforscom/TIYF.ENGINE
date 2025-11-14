using System.Globalization;
using System.Linq;
using System.Text;

namespace TiYf.Engine.Host;

public static class EngineMetricsFormatter
{
    public static string Format(EngineMetricsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendMetric(builder, "engine_heartbeat_age_seconds", snapshot.HeartbeatAgeSeconds);
        AppendMetric(builder, "engine_bar_lag_ms", snapshot.BarLagMilliseconds);
        AppendMetric(builder, "engine_pending_orders", snapshot.PendingOrders);
        AppendMetric(builder, "engine_open_positions", snapshot.OpenPositions);
        AppendMetric(builder, "engine_active_orders", snapshot.ActiveOrders);
        AppendMetric(builder, "engine_risk_events_total", snapshot.RiskEventsTotal);
        AppendMetric(builder, "engine_alerts_total", snapshot.AlertsTotal);
        AppendMetric(builder, "engine_order_rejects_total", snapshot.OrderRejectsTotal);
        AppendMetric(builder, "engine_stream_connected", snapshot.StreamConnected);
        AppendMetric(builder, "engine_stream_heartbeat_age_seconds", snapshot.StreamHeartbeatAgeSeconds);
        AppendMetric(builder, "engine_loop_uptime_seconds", snapshot.LoopUptimeSeconds);
        AppendMetric(builder, "engine_loop_iterations_total", snapshot.LoopIterationsTotal);
        AppendMetric(builder, "engine_decisions_total", snapshot.DecisionsTotal);
        AppendMetric(builder, "engine_loop_last_success_ts", snapshot.LoopLastSuccessUnixSeconds);
        AppendMetric(builder, "engine_risk_blocks_total", snapshot.RiskBlocksTotal);
        foreach (var kvp in snapshot.RiskBlocksByGate)
        {
            AppendMetric(builder, "engine_risk_blocks_total", kvp.Value, "gate", kvp.Key);
        }
        AppendMetric(builder, "engine_risk_throttles_total", snapshot.RiskThrottlesTotal);
        foreach (var kvp in snapshot.RiskThrottlesByGate)
        {
            AppendMetric(builder, "engine_risk_throttles_total", kvp.Value, "gate", kvp.Key);
        }
        foreach (var kvp in snapshot.LastOrderSizeBySymbol)
        {
            AppendMetric(builder, "engine_last_order_size_units", kvp.Value, "instrument", kvp.Key);
        }
        var idempotencyTotal = snapshot.IdempotencyCacheSizes.Values.Sum();
        AppendMetric(builder, "engine_idempotency_cache_size", idempotencyTotal);
        foreach (var kvp in snapshot.IdempotencyCacheSizes)
        {
            AppendMetric(builder, "engine_idempotency_cache_size", kvp.Value, "kind", kvp.Key);
        }
        AppendMetric(builder, "engine_idempotency_evictions_total", snapshot.IdempotencyEvictionsTotal);
        AppendMetric(builder, "engine_idempotency_persisted_loaded", snapshot.IdempotencyPersistedLoaded);
        AppendMetric(builder, "engine_idempotency_persisted_expired_total", snapshot.IdempotencyPersistedExpired);
        if (snapshot.IdempotencyPersistenceLastLoadUnix.HasValue)
        {
            AppendMetric(builder, "engine_idempotency_persistence_last_load_ts", snapshot.IdempotencyPersistenceLastLoadUnix.Value);
        }
        if (!string.IsNullOrWhiteSpace(snapshot.SlippageModel))
        {
            AppendMetric(builder, "engine_slippage_model", 1, "model", snapshot.SlippageModel);
        }
        if (snapshot.SlippageLastPriceDelta.HasValue)
        {
            AppendMetric(builder, "engine_slippage_last_price_delta", snapshot.SlippageLastPriceDelta.Value);
        }
        AppendMetric(builder, "engine_slippage_adjusted_orders_total", snapshot.SlippageAdjustedOrdersTotal);
        AppendMetric(builder, "engine_news_events_fetched_total", snapshot.NewsEventsFetchedTotal);
        AppendMetric(builder, "engine_news_blackout_windows_total", snapshot.NewsBlackoutWindowsActive);
        if (!string.IsNullOrWhiteSpace(snapshot.NewsSourceType))
        {
            AppendMetric(builder, "engine_news_source", 1, "type", snapshot.NewsSourceType);
        }
        if (snapshot.NewsLastEventUnixSeconds.HasValue)
        {
            AppendMetric(builder, "engine_news_last_event_ts", snapshot.NewsLastEventUnixSeconds.Value);
        }
        if (!string.IsNullOrWhiteSpace(snapshot.PromotionConfigHash))
        {
            AppendMetric(builder, "engine_promotion_config_hash", 1, "hash", snapshot.PromotionConfigHash);
        }
        if (snapshot.PromotionTelemetry is { } promotion)
        {
            AppendMetric(builder, "engine_promotion_candidates_total", promotion.CandidatesTotal);
            AppendMetric(builder, "engine_promotion_probation_days", promotion.ProbationDays);
            AppendMetric(builder, "engine_promotion_min_trades", promotion.MinTrades);
            AppendMetric(builder, "engine_promotion_threshold", (double)promotion.PromotionThreshold);
            AppendMetric(builder, "engine_demotion_threshold", (double)promotion.DemotionThreshold);
        }
        AppendMetric(builder, "engine_reconcile_mismatches_total", snapshot.ReconciliationMismatchesTotal);
        if (snapshot.ReconciliationLastUnixSeconds.HasValue)
        {
            AppendMetric(builder, "engine_reconcile_last_ts", snapshot.ReconciliationLastUnixSeconds.Value);
        }
        if (!string.IsNullOrWhiteSpace(snapshot.ReconciliationLastStatus))
        {
            AppendMetric(builder, "engine_reconcile_last_status", 1, "status", snapshot.ReconciliationLastStatus!);
        }
        if (snapshot.GvrsRaw.HasValue)
        {
            AppendMetric(builder, "engine_gvrs_raw", snapshot.GvrsRaw.Value);
        }
        if (snapshot.GvrsEwma.HasValue)
        {
            AppendMetric(builder, "engine_gvrs_ewma", snapshot.GvrsEwma.Value);
        }
        if (!string.IsNullOrWhiteSpace(snapshot.GvrsBucket))
        {
            AppendMetric(builder, "engine_gvrs_bucket", 1d, "bucket", snapshot.GvrsBucket!);
        }
        return builder.ToString();
    }

    private static void AppendMetric(StringBuilder builder, string name, double value)
    {
        builder.Append(name)
            .Append(' ')
            .AppendFormat(CultureInfo.InvariantCulture, "{0}", value)
            .Append('\n');
    }

    private static void AppendMetric(StringBuilder builder, string name, long value)
    {
        builder.Append(name)
            .Append(' ')
            .Append(value)
            .Append('\n');
    }

    private static void AppendMetric(StringBuilder builder, string name, int value)
        => AppendMetric(builder, name, (long)value);

    private static void AppendMetric(StringBuilder builder, string name, long value, string labelName, string labelValue)
    {
        builder.Append(name)
            .Append('{')
            .Append(labelName)
            .Append("=\"")
            .Append(labelValue.Replace("\"", "\\\""))
            .Append("\"} ")
            .Append(value)
            .Append('\n');
    }

    private static void AppendMetric(StringBuilder builder, string name, double value, string labelName, string labelValue)
    {
        builder.Append(name)
            .Append('{')
            .Append(labelName)
            .Append("=\"")
            .Append(labelValue.Replace("\"", "\\\""))
            .Append("\"} ")
            .AppendFormat(CultureInfo.InvariantCulture, "{0}", value)
            .Append('\n');
    }
}
