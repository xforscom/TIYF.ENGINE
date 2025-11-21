using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Host;
using TiYf.Engine.Sim;

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
        state.RegisterAlert("adapter");
        state.RegisterAlert("risk_rails");
        state.RecordStreamHeartbeat(DateTime.UtcNow);
        state.UpdateStreamConnection(true);
        state.RecordLoopDecision("H1", decisionTime);
        state.SetGvrsSnapshot(new MarketContextService.GvrsSnapshot(0.25m, 0.20m, "calm", "shadow", true));
        state.SetGvrsGateConfig(true, false);
        state.RegisterGvrsGateBlock(decisionTime);
        state.RegisterOrderAccepted("EURUSD", 250);
        state.RegisterOrderRejected();
        state.UpdateIdempotencyMetrics(2, 1, 3);
        state.SetIdempotencyPersistenceStats(4, 1, DateTime.UtcNow);
        state.SetSlippageModel("fixed_bps");
        state.RecordSlippage(0.0002m);
        var lastNews = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        state.UpdateNewsTelemetry(lastNews, 3, true, lastNews.AddMinutes(-15), lastNews.AddMinutes(15), "file");
        state.RecordReconciliationTelemetry(ReconciliationStatus.Match, 0, DateTime.UtcNow);
        state.SetConfigSource("/tmp/sample-config.json", "hash-demo", "demo-config-id");
        state.SetRiskConfigHash("riskhash");
        state.UpdateSecretProvenance(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["oanda_demo"] = new[] { "env" }
        });
        var cooldownUntil = DateTime.UtcNow.AddMinutes(10);
        state.UpdateRiskRailsTelemetry(new RiskRailTelemetrySnapshot(
            BrokerDailyLossCapCcy: 2500m,
            BrokerDailyLossUsedCcy: 1250m,
            BrokerDailyLossViolationsTotal: 1,
            MaxPositionUnitsLimit: 500000,
            MaxPositionUnitsUsed: 250000,
            MaxPositionViolationsTotal: 2,
            SymbolUnitCaps: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["EURUSD"] = 300000
            },
            SymbolUnitUsage: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["EURUSD"] = 150000
            },
            SymbolUnitViolations: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["EURUSD"] = 1
            },
            BrokerCapBlocksTotal: 2,
            BrokerCapBlocksByGate: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["daily_loss"] = 1,
                ["symbol_units:EURUSD"] = 1
            },
            CooldownEnabled: true,
            CooldownActive: true,
            CooldownActiveUntilUtc: cooldownUntil,
            CooldownTriggersTotal: 3,
            CooldownConsecutiveLosses: 4,
            CooldownMinutes: 30));

        var snapshot = state.CreateMetricsSnapshot();
        var metricsText = EngineMetricsFormatter.Format(snapshot);

        Assert.Contains("engine_heartbeat_age_seconds", metricsText);
        Assert.Contains("engine_bar_lag_ms", metricsText);
        Assert.Contains("engine_open_positions 2", metricsText);
        Assert.Contains("engine_alerts_total 6", metricsText);
        Assert.Contains("engine_alerts_total{category=\"adapter\"} 1", metricsText);
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
        Assert.Contains("engine_news_source{type=\"file\"} 1", metricsText);
        Assert.Contains("engine_news_last_event_ts", metricsText);
        Assert.Contains("engine_config_hash{hash=\"hash-demo\"} 1", metricsText);
        Assert.Contains("engine_config_id{config_id=\"demo-config-id\"} 1", metricsText);
        Assert.Contains("engine_risk_config_hash{hash=\"riskhash\"} 1", metricsText);
        Assert.Contains("engine_risk_broker_daily_cap_ccy 2500", metricsText);
        Assert.Contains("engine_risk_broker_daily_loss_used_ccy 1250", metricsText);
        Assert.Contains("engine_risk_broker_daily_cap_violations_total 1", metricsText);
        Assert.Contains("engine_risk_max_position_units_limit 500000", metricsText);
        Assert.Contains("engine_risk_max_position_units_used 250000", metricsText);
        Assert.Contains("engine_risk_symbol_unit_cap_limit{instrument=\"EURUSD\"} 300000", metricsText);
        Assert.Contains("engine_risk_symbol_unit_cap_used{instrument=\"EURUSD\"} 150000", metricsText);
        Assert.Contains("engine_risk_symbol_unit_cap_violations_total{instrument=\"EURUSD\"} 1", metricsText);
        Assert.Contains("engine_broker_cap_blocks_total 2", metricsText);
        Assert.Contains("engine_broker_cap_blocks_total{gate=\"daily_loss\"} 1", metricsText);
        Assert.Contains("engine_risk_cooldown_active 1", metricsText);
        Assert.Contains("engine_risk_cooldown_triggers_total 3", metricsText);
        Assert.Contains("engine_gvrs_gate_blocking_enabled 0", metricsText);
        Assert.Contains("engine_gvrs_gate_blocks_total 1", metricsText);
        Assert.Contains("engine_gvrs_gate_is_blocking{state=\"volatile\"} 0", metricsText);
        Assert.Contains("engine_secret_provenance{integration=\"oanda_demo\",source=\"env\"} 1", metricsText);
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
        state.SetGvrsGateConfig(true, true);
        state.RegisterGvrsGateBlock(decisionTime);
        state.RegisterOrderAccepted("GBPUSD", 75);
        state.RegisterOrderRejected();
        state.UpdateIdempotencyMetrics(5, 4, 7);
        state.SetIdempotencyPersistenceStats(2, 1, DateTime.UtcNow);
        state.SetSlippageModel("fixed_bps");
        state.RecordSlippage(-0.00015m);
        var newsNow = new DateTime(2025, 2, 2, 6, 30, 0, DateTimeKind.Utc);
        state.UpdateNewsTelemetry(newsNow, 5, false, null, null, "file");
        state.RecordReconciliationTelemetry(ReconciliationStatus.Mismatch, 2, DateTime.UtcNow);
        state.SetConfigSource("/opt/config.json", "cfg-hash");
        state.SetRiskConfigHash("risk-hash");
        state.UpdateSecretProvenance(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["ctrader_demo"] = new[] { "env", "missing" }
        });
        var cooldownUntil = DateTime.UtcNow.AddMinutes(20);
        state.UpdateRiskRailsTelemetry(new RiskRailTelemetrySnapshot(
            BrokerDailyLossCapCcy: 2000m,
            BrokerDailyLossUsedCcy: 800m,
            BrokerDailyLossViolationsTotal: 0,
            MaxPositionUnitsLimit: 400000,
            MaxPositionUnitsUsed: 100000,
            MaxPositionViolationsTotal: 0,
            SymbolUnitCaps: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["GBPUSD"] = 150000
            },
            SymbolUnitUsage: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["GBPUSD"] = 75000
            },
            SymbolUnitViolations: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["GBPUSD"] = 0
            },
            BrokerCapBlocksTotal: 1,
            BrokerCapBlocksByGate: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["global_units"] = 1
            },
            CooldownEnabled: true,
            CooldownActive: false,
            CooldownActiveUntilUtc: cooldownUntil,
            CooldownTriggersTotal: 1,
            CooldownConsecutiveLosses: 3,
            CooldownMinutes: 20));
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
        var gvrsGate = root.GetProperty("gvrs_gate");
        Assert.Equal("Moderate", gvrsGate.GetProperty("bucket").GetString());
        Assert.True(gvrsGate.GetProperty("blocking_enabled").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(gvrsGate.GetProperty("last_block_utc").GetString()));
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
        Assert.Equal("file", news.GetProperty("source_type").GetString());
        var config = root.GetProperty("config");
        Assert.Equal("/opt/config.json", config.GetProperty("path").GetString());
        Assert.Equal("cfg-hash", config.GetProperty("hash").GetString());
        var secrets = root.GetProperty("secrets");
        Assert.Equal("env,missing", secrets.GetProperty("ctrader_demo").GetString());
        Assert.Equal(75, lastOrderSizes.GetProperty("GBPUSD").GetInt64());
        var reconciliation = root.GetProperty("reconciliation");
        Assert.Equal(2, reconciliation.GetProperty("mismatches_total").GetInt64());
        Assert.Equal("mismatch", reconciliation.GetProperty("last_status").GetString());
        var riskRails = root.GetProperty("risk_rails");
        Assert.Equal(2000, riskRails.GetProperty("broker_daily_cap_ccy").GetDecimal());
        Assert.Equal(800, riskRails.GetProperty("broker_daily_loss_used_ccy").GetDecimal());
        Assert.Equal(400000, riskRails.GetProperty("max_position_units").GetInt64());
        Assert.Equal(100000, riskRails.GetProperty("max_position_units_used").GetInt64());
        var symbolCaps = riskRails.GetProperty("symbol_caps");
        Assert.Equal(150000, symbolCaps.GetProperty("GBPUSD").GetInt64());
        Assert.Equal(1, riskRails.GetProperty("gvrs_gate_blocks_total").GetInt64());
        var cooldown = riskRails.GetProperty("cooldown");
        Assert.True(cooldown.GetProperty("enabled").GetBoolean());
        Assert.False(cooldown.GetProperty("active").GetBoolean());
        Assert.Equal(1, cooldown.GetProperty("triggers_total").GetInt64());
        var persistence = root.GetProperty("idempotency_persistence");
        Assert.Equal(2, persistence.GetProperty("loaded_keys").GetInt32());
        Assert.Equal(1, persistence.GetProperty("expired_dropped").GetInt32());
    }
}
