using System.Text.Json;

namespace OpsDashboard.Services;

public sealed class HealthSnapshot
{
    public string Adapter { get; init; } = string.Empty;
    public bool Connected { get; init; }
    public double? HeartbeatAgeSeconds { get; init; }
    public double? StreamHeartbeatAgeSeconds { get; init; }
    public int StreamConnected { get; init; }
    public double? BarLagMs { get; init; }
    public int OpenPositions { get; init; }
    public int ActiveOrders { get; init; }
    public string? GvrsBucket { get; init; }
    public double? GvrsRaw { get; init; }
    public double? GvrsEwma { get; init; }
    public string ConfigId { get; init; } = string.Empty;
    public string ConfigPath { get; init; } = string.Empty;
    public string ConfigHash { get; init; } = string.Empty;
    public string RiskConfigHash { get; init; } = string.Empty;
    public string PromotionConfigHash { get; init; } = string.Empty;
    public long? RiskBlocksTotal { get; init; }
    public long? RiskThrottlesTotal { get; init; }
    public long? BrokerCapBlocksTotal { get; init; }
    public long? GvrsGateBlocksTotal { get; init; }
    public long? AlertsTotal { get; init; }
    public int? PromotionCandidates { get; init; }
    public int? PromotionProbationDays { get; init; }
    public int? PromotionMinTrades { get; init; }
    public long? DecisionsTotal { get; init; }
    public string? ReconcileStatus { get; init; }
    public long? ReconcileMismatchesTotal { get; init; }

    public static HealthSnapshot FromJson(JsonDocument doc)
    {
        var root = doc.RootElement;
        var config = root.TryGetProperty("config", out var cfg) ? cfg : default;
        var riskRails = root.TryGetProperty("risk_rails", out var rr) ? rr : default;
        var promotion = root.TryGetProperty("promotion", out var promo) ? promo : default;
        var recon = root.TryGetProperty("reconciliation", out var rec) ? rec : default;
        var gvrsGateBlocks = riskRails.ValueKind == JsonValueKind.Object && riskRails.TryGetProperty("gvrs_gate_blocks_total", out var ggb)
            ? ggb.GetInt64()
            : (long?)null;

        return new HealthSnapshot
        {
            Adapter = root.TryGetProperty("adapter", out var a) ? a.GetString() ?? string.Empty : string.Empty,
            Connected = root.TryGetProperty("connected", out var c) && c.GetBoolean(),
            HeartbeatAgeSeconds = root.TryGetProperty("heartbeat_age_seconds", out var h) ? h.GetDouble() : (double?)null,
            StreamConnected = root.TryGetProperty("stream_connected", out var sc) ? sc.GetInt32() : 0,
            StreamHeartbeatAgeSeconds = root.TryGetProperty("stream_heartbeat_age_seconds", out var sh) ? sh.GetDouble() : (double?)null,
            BarLagMs = root.TryGetProperty("bar_lag_ms", out var b) ? b.GetDouble() : (double?)null,
            OpenPositions = root.TryGetProperty("open_positions", out var op) ? op.GetInt32() : 0,
            ActiveOrders = root.TryGetProperty("active_orders", out var ao) ? ao.GetInt32() : 0,
            GvrsBucket = root.TryGetProperty("gvrs_bucket", out var gb) ? gb.GetString() : null,
            GvrsRaw = root.TryGetProperty("gvrs_raw", out var gr) && gr.ValueKind != JsonValueKind.Null ? gr.GetDouble() : (double?)null,
            GvrsEwma = root.TryGetProperty("gvrs_ewma", out var ge) && ge.ValueKind != JsonValueKind.Null ? ge.GetDouble() : (double?)null,
            ConfigId = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("id", out var cid) ? cid.GetString() ?? string.Empty : string.Empty,
            ConfigPath = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("path", out var cp) ? cp.GetString() ?? string.Empty : string.Empty,
            ConfigHash = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("hash", out var ch) ? ch.GetString() ?? string.Empty : string.Empty,
            RiskConfigHash = root.TryGetProperty("risk_config_hash", out var rch) ? rch.GetString() ?? string.Empty : string.Empty,
            PromotionConfigHash = root.TryGetProperty("promotion_config_hash", out var pch) ? pch.GetString() ?? string.Empty : string.Empty,
            RiskBlocksTotal = root.TryGetProperty("risk_blocks_total", out var rbt) ? rbt.GetInt64() : (long?)null,
            RiskThrottlesTotal = root.TryGetProperty("risk_throttles_total", out var rtt) ? rtt.GetInt64() : (long?)null,
            BrokerCapBlocksTotal = riskRails.ValueKind == JsonValueKind.Object && riskRails.TryGetProperty("broker_cap_blocks_total", out var bcb) ? bcb.GetInt64() : (long?)null,
            GvrsGateBlocksTotal = gvrsGateBlocks,
            AlertsTotal = root.TryGetProperty("alerts_total", out var at) ? at.GetInt64() : (long?)null,
            PromotionCandidates = promotion.ValueKind == JsonValueKind.Object && promotion.TryGetProperty("candidates", out var pc) ? pc.GetArrayLength() : (int?)null,
            PromotionProbationDays = promotion.ValueKind == JsonValueKind.Object && promotion.TryGetProperty("probation_days", out var ppd) ? ppd.GetInt32() : (int?)null,
            PromotionMinTrades = promotion.ValueKind == JsonValueKind.Object && promotion.TryGetProperty("min_trades", out var pmt) ? pmt.GetInt32() : (int?)null,
            DecisionsTotal = root.TryGetProperty("decisions_total", out var dt) ? dt.GetInt64() : (long?)null,
            ReconcileStatus = recon.ValueKind == JsonValueKind.Object && recon.TryGetProperty("last_status", out var rs) ? rs.GetString() : null,
            ReconcileMismatchesTotal = recon.ValueKind == JsonValueKind.Object && recon.TryGetProperty("mismatches_total", out var rmt) ? rmt.GetInt64() : (long?)null
        };
    }

    public (string Status, string BadgeClass) EvaluateStatus()
    {
        if (!Connected || StreamConnected != 1)
        {
            return ("Down", "danger");
        }
        var heartbeatOk = HeartbeatAgeSeconds is null || HeartbeatAgeSeconds < 10;
        var barLagOk = BarLagMs is null || BarLagMs < 300000; // 5 minutes
        if (heartbeatOk && barLagOk)
        {
            return ("Healthy", "success");
        }
        return ("Degraded", "warning");
    }
}
