using System.Globalization;
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
