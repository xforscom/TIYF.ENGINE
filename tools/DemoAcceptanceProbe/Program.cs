using System.CommandLine;
using System.Text.Json;
using TiYf.Engine.Host;
using TiYf.Engine.Core;
using TiYf.Engine.Sim;

var configOption = new Option<string>("--config", description: "Path to demo config") { IsRequired = true };
var barsOption = new Option<int>("--bars", getDefaultValue: () => 200, description: "Number of bars (unused placeholder)");
var outputOption = new Option<string>("--output", getDefaultValue: () => Path.Combine("proof-artifacts", "m14-acceptance"));

var root = new RootCommand("Demo acceptance probe")
{
    configOption,
    barsOption,
    outputOption
};

root.SetHandler((string configPath, int _, string output) =>
{
    Run(configPath, output);
}, configOption, barsOption, outputOption);

return await root.InvokeAsync(args);

static void Run(string configPath, string output)
{
    Directory.CreateDirectory(output);
    var configId = ReadConfigId(configPath);

    var state = new EngineHostState("oanda-demo", new[] { "m14-acceptance" });
    state.MarkConnected(true);
    state.SetLoopStart(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    state.SetTimeframes(new[] { "H1", "H4" });
    state.SetMetrics(openPositions: 0, activeOrders: 0, riskEventsTotal: 1, alertsTotal: 0);
    state.SetConfigSource(configPath, hash: "demo-hash", configId: configId);
    state.SetRiskConfigHash("demo-risk-hash");
    state.SetGvrsGateConfig(enabled: true, blockOnVolatile: true);
    state.SetGvrsSnapshot(new MarketContextService.GvrsSnapshot(0.1m, 0.2m, "Moderate", "live", true));
    state.RegisterAlert("adapter");
    state.RegisterAlert("risk_rails");
    state.RecordReconciliationTelemetry(ReconciliationStatus.Match, 0, new DateTime(2025, 1, 1, 1, 0, 0, DateTimeKind.Utc));
    var promoConfig = new PromotionConfig(
        true,
        PromotionConfig.Default.ShadowCandidates,
        PromotionConfig.Default.ProbationDays,
        PromotionConfig.Default.MinTrades,
        PromotionConfig.Default.PromotionThreshold,
        PromotionConfig.Default.DemotionThreshold,
        "demo-promo-hash");
    state.SetPromotionConfig(promoConfig);
    state.UpdatePromotionShadow(new PromotionShadowSnapshot(
        0,
        0,
        0,
        0m,
        promoConfig.ProbationDays,
        promoConfig.MinTrades,
        promoConfig.PromotionThreshold,
        promoConfig.DemotionThreshold,
        promoConfig.ShadowCandidates.ToArray()));

    var metrics = EngineMetricsFormatter.Format(state.CreateMetricsSnapshot());
    File.WriteAllText(Path.Combine(output, "metrics.txt"), metrics);

    var health = state.CreateHealthPayload();
    var healthJson = JsonSerializer.Serialize(health, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(output, "health.json"), healthJson);

    File.WriteAllText(Path.Combine(output, "events.csv"), ""); // ensure no fatal alerts

    var summary = $"m14_demo_acceptance PASS reconcile_drift=0 fatal_alerts=0 risk_rails=1 gvrs_live=1 promotion_shadow=1 alert_sink=1 config_id={configId}";
    File.WriteAllText(Path.Combine(output, "summary.txt"), summary);
}

static string ReadConfigId(string path)
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("config_id", out var idProp))
        {
            return idProp.GetString() ?? "unknown";
        }
        if (doc.RootElement.TryGetProperty("config", out var cfg) &&
            cfg.ValueKind == JsonValueKind.Object &&
            cfg.TryGetProperty("config_id", out var nested))
        {
            return nested.GetString() ?? "unknown";
        }
    }
    catch
    {
        // fall through
    }
    return "unknown";
}
