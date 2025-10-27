using System.Globalization;
using System.Text;

namespace TiYf.Engine.Host;

internal static class EngineMetricsFormatter
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
}
