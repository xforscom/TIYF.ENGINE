using System.Text.Json;
using TiYf.Engine.Host;
using TiYf.Engine.Host.Alerts;

var output = ParseArgs(args);
Directory.CreateDirectory(output);

var alertsPath = Path.Combine(output, "alerts.log");
var sink = new FileAlertSink(alertsPath);

var state = new EngineHostState("alert-proof", Array.Empty<string>());
var fixedNow = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

Emit("adapter", "error", "adapter_disconnected");
Emit("risk_rails", "error", "risk_block_hard");
Emit("reconcile", "warn", "reconcile_mismatch");

var summary = $"alerts_total=3 adapter=1 risk_rails=1 reconcile=1 ts={fixedNow:o}";
File.WriteAllText(Path.Combine(output, "summary.txt"), summary);

var metrics = EngineMetricsFormatter.Format(state.CreateMetricsSnapshot());
File.WriteAllText(Path.Combine(output, "metrics.txt"), metrics);

var healthPayload = state.CreateHealthPayload();
var json = JsonSerializer.Serialize(healthPayload, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(output, "health.json"), json);

return;

void Emit(string category, string severity, string summaryText)
{
    var alert = new AlertRecord(category, severity, summaryText, null, fixedNow);
    state.RegisterAlert(category);
    sink.Enqueue(alert);
}

static string ParseArgs(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if ((args[i] == "--output" || args[i] == "-o") && i + 1 < args.Length)
        {
            return args[i + 1];
        }
    }

    return Path.Combine("proof-artifacts", "m13-alerting");
}
