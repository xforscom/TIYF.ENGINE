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
        AppendMetric(builder, "engine_broker_cap_blocks_total", snapshot.RiskBrokerCapBlocksTotal);
        foreach (var kvp in snapshot.RiskBrokerCapBlocksByGate)
        {
            AppendMetric(builder, "engine_broker_cap_blocks_total", kvp.Value, "gate", kvp.Key);
        }
        if (snapshot.RiskBrokerDailyLossCapCcy.HasValue)
        {
            AppendMetric(builder, "engine_risk_broker_daily_cap_ccy", (double)snapshot.RiskBrokerDailyLossCapCcy.Value);
        }
        AppendMetric(builder, "engine_risk_broker_daily_loss_used_ccy", (double)snapshot.RiskBrokerDailyLossUsedCcy);
        AppendMetric(builder, "engine_risk_broker_daily_cap_violations_total", snapshot.RiskBrokerDailyLossViolationsTotal);
        if (snapshot.RiskMaxPositionUnitsLimit.HasValue)
        {
            AppendMetric(builder, "engine_risk_max_position_units_limit", snapshot.RiskMaxPositionUnitsLimit.Value);
        }
        AppendMetric(builder, "engine_risk_max_position_units_used", snapshot.RiskMaxPositionUnitsUsed);
        AppendMetric(builder, "engine_risk_max_position_violations_total", snapshot.RiskMaxPositionViolationsTotal);
        foreach (var usage in snapshot.RiskSymbolCapUsage)
        {
            AppendMetric(builder, "engine_risk_symbol_unit_cap_used", usage.Value, "instrument", usage.Key);
        }
        if (snapshot.RiskSymbolCapLimits is not null)
        {
            foreach (var cap in snapshot.RiskSymbolCapLimits)
            {
                AppendMetric(builder, "engine_risk_symbol_unit_cap_limit", cap.Value, "instrument", cap.Key);
            }
        }
        foreach (var violation in snapshot.RiskSymbolCapViolations)
        {
            AppendMetric(builder, "engine_risk_symbol_unit_cap_violations_total", violation.Value, "instrument", violation.Key);
        }
        AppendMetric(builder, "engine_risk_cooldown_active", snapshot.RiskCooldownActive ? 1 : 0);
        AppendMetric(builder, "engine_risk_cooldown_triggers_total", snapshot.RiskCooldownTriggersTotal);
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
        if (!string.IsNullOrWhiteSpace(snapshot.ConfigHash))
        {
            AppendMetric(builder, "engine_config_hash", 1, "hash", snapshot.ConfigHash);
        }
        if (!string.IsNullOrWhiteSpace(snapshot.RiskConfigHash))
        {
            AppendMetric(builder, "engine_risk_config_hash", 1, "hash", snapshot.RiskConfigHash);
        }
        if (snapshot.SecretProvenance is { Count: > 0 })
        {
            foreach (var kvp in snapshot.SecretProvenance)
            {
                foreach (var source in kvp.Value ?? Array.Empty<string>())
                {
                    AppendMetric(builder, "engine_secret_provenance", 1, ("integration", kvp.Key), ("source", source));
                }
            }
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
        AppendMetric(builder, "engine_promotion_shadow_promotions_total", snapshot.PromotionShadowPromotionsTotal);
        AppendMetric(builder, "engine_promotion_shadow_demotions_total", snapshot.PromotionShadowDemotionsTotal);
        AppendMetric(builder, "engine_promotion_shadow_trades_total", snapshot.PromotionShadowTradesTotal);
        AppendMetric(builder, "engine_promotion_shadow_win_ratio", (double)snapshot.PromotionShadowWinRatio);
        AppendMetric(builder, "engine_reconcile_mismatches_total", snapshot.ReconciliationMismatchesTotal);
        AppendMetric(builder, "engine_reconcile_runs_total", snapshot.ReconciliationRunsTotal);
        if (snapshot.ReconciliationLastDurationSeconds.HasValue)
        {
            AppendMetric(builder, "engine_reconcile_last_duration_seconds", snapshot.ReconciliationLastDurationSeconds.Value);
        }
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
        AppendMetric(builder, "engine_gvrs_gate_blocking_enabled", snapshot.GvrsGateBlockingEnabled ? 1 : 0);
        AppendMetric(builder, "engine_gvrs_gate_blocks_total", snapshot.GvrsGateBlocksTotal);
        var blockingValue = snapshot.GvrsGateWouldBlock ? 1 : 0;
        AppendMetric(builder, "engine_gvrs_gate_is_blocking", blockingValue, "state", "volatile");
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

    private static void AppendMetric(StringBuilder builder, string name, long value, params (string Name, string Value)[] labels)
    {
        if (labels is null || labels.Length == 0)
        {
            AppendMetric(builder, name, value);
            return;
        }

        builder.Append(name).Append('{');
        for (var i = 0; i < labels.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }
            builder.Append(labels[i].Name)
                .Append("=\"")
                .Append(labels[i].Value.Replace("\"", "\\\""))
                .Append('"');
        }
        builder.Append("} ")
            .Append(value)
            .Append('\n');
    }
}
