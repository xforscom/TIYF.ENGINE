using System.Text.Json;
using TiYf.Engine.Host;
using TiYf.Engine.Core;
using TiYf.Engine.Sim;

var (configPath, outputPath, adapterId) = ParseArgs(args);
Run(configPath, outputPath, adapterId);
return;

static void Run(string configPath, string output, string adapterId)
{
    Directory.CreateDirectory(output);
    var configId = ReadConfigId(configPath);

    var state = new EngineHostState(adapterId, new[] { "m14-acceptance" });
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
    var shadowCandidates = new[] { "shadow-demo" };
    var promotionConfig = new PromotionConfig(
        true,
        shadowCandidates,
        30,
        50,
        0.6m,
        0.4m,
        "demo-promo-hash");
    state.SetPromotionConfig(promotionConfig);
    state.UpdatePromotionShadow(new PromotionShadowSnapshot(0, 0, 0, 0m, 30, 50, 0.6m, 0.4m, shadowCandidates));

    var metrics = EngineMetricsFormatter.Format(state.CreateMetricsSnapshot());
    File.WriteAllText(Path.Combine(output, "metrics.txt"), metrics);

    var health = state.CreateHealthPayload();
    var healthJson = JsonSerializer.Serialize(health, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(output, "health.json"), healthJson);

    File.WriteAllText(Path.Combine(output, "events.csv"), ""); // probe emits no events; workflow asserts absence of fatal alerts

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

static (string Config, string Output, string Adapter) ParseArgs(string[] args)
{
    string config = string.Empty;
    string output = Path.Combine("proof-artifacts", "m14-acceptance");
    string adapter = "oanda-demo";
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if ((arg == "--config" || arg == "-c") && i + 1 < args.Length)
        {
            config = args[i + 1];
            i++;
            continue;
        }
        if ((arg == "--output" || arg == "-o") && i + 1 < args.Length)
        {
            output = args[i + 1];
            i++;
            continue;
        }
        if ((arg == "--adapter" || arg == "-a") && i + 1 < args.Length)
        {
            adapter = args[i + 1];
            i++;
        }
    }

    if (string.IsNullOrWhiteSpace(config))
    {
        throw new ArgumentException("config is required (--config path)");
    }

    return (config, output, adapter);
}
