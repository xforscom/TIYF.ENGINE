using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Host;

static string ResolveOutputDirectory(string[] args)
{
    const string defaultDir = "artifacts";
    for (int i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(args[i], "--output", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException("Expected path after --out/--output");
            }
            return args[i + 1];
        }
    }
    return defaultDir;
}

static Bar CreateBar(string symbol, DateTime startUtc, decimal open, decimal high, decimal low, decimal close)
    => new(new InstrumentId(symbol), startUtc, startUtc.AddHours(1), open, high, low, close, 1m);

static DateTime ParseUtc(string iso)
    => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

static void WriteFile(string path, string contents)
{
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }
    File.WriteAllText(path, contents);
}

static void WriteLines(string path, IEnumerable<string> lines)
{
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }
    File.WriteAllLines(path, lines);
}

var outputDir = Path.GetFullPath(ResolveOutputDirectory(args).Trim());
Directory.CreateDirectory(outputDir);

var config = new GlobalVolatilityGateConfig(
    EnabledMode: "shadow",
    EntryThreshold: 1.1m,
    EwmaAlpha: 0.3m,
    new List<GlobalVolatilityComponentConfig>
    {
        new("fx_atr_percentile", 1.0m)
    });

var service = new MarketContextService(config, atrLookbackHours: 3, atrPercentileHours: 3, proxyLookbackHours: 3);
var start = ParseUtc("2025-11-01T00:00:00Z");
service.OnBar(CreateBar("EURUSD", start, 1.0m, 1.02m, 0.98m, 1.01m), BarInterval.OneHour);
service.OnBar(CreateBar("EURUSD", start.AddHours(1), 1.01m, 1.05m, 0.97m, 1.03m), BarInterval.OneHour);
service.OnBar(CreateBar("EURUSD", start.AddHours(2), 1.03m, 1.12m, 0.90m, 1.10m), BarInterval.OneHour);

if (!service.HasValue)
{
    throw new InvalidOperationException("GVRS service failed to produce a snapshot after seed bars.");
}

var evaluation = service.Evaluate(config);
if (!evaluation.ShouldAlert)
{
    throw new InvalidOperationException("Expected GVRS evaluation to trigger a shadow alert for the seeded bars.");
}

var alertManager = new GvrsShadowAlertManager();
if (!alertManager.TryRegister("GVRS-DEMO-001", evaluation.ShouldAlert))
{
    throw new InvalidOperationException("Shadow alert manager rejected the decision unexpectedly.");
}

var decisionUtc = start.AddHours(3);
var payload = new
{
    instrument = "EURUSD",
    timeframe = "H1",
    ts = decisionUtc,
    gvrs_raw = decimal.ToDouble(evaluation.Raw),
    gvrs_ewma = decimal.ToDouble(evaluation.Ewma),
    gvrs_bucket = evaluation.Bucket,
    entry_threshold = (double)config.EntryThreshold,
    mode = evaluation.Mode
};
var payloadJson = JsonSerializer.Serialize(payload);
var csvPath = Path.Combine(outputDir, "events.csv");
var csvLines = new[]
{
    "sequence,utc_ts,event_type,src_adapter,payload_json",
    $"1,{decisionUtc:O},ALERT_SHADOW_GVRS_GATE,oanda-demo,\"{payloadJson.Replace("\"", "\"\"")}\""
};
WriteLines(csvPath, csvLines);

var state = new EngineHostState("oanda-demo", Array.Empty<string>());
state.MarkConnected(true);
state.SetLoopStart(start);
state.SetTimeframes(new[] { "H1" });
state.RecordLoopDecision("H1", decisionUtc);
state.SetMetrics(openPositions: 0, activeOrders: 0, riskEventsTotal: 1, alertsTotal: 1);
state.SetGvrsSnapshot(service.Snapshot);

var health = state.CreateHealthPayload();
var healthJson = JsonSerializer.Serialize(health, new JsonSerializerOptions { WriteIndented = true });
WriteFile(Path.Combine(outputDir, "health.json"), healthJson);

var metricsSnapshot = state.CreateMetricsSnapshot();
var metricsText = EngineMetricsFormatter.Format(metricsSnapshot);
WriteFile(Path.Combine(outputDir, "metrics.txt"), metricsText);

var summaryLine = $"gvrs_proof: decision=GVRS-DEMO-001 gvrs_raw={decimal.ToDouble(evaluation.Raw):0.000} gvrs_ewma={decimal.ToDouble(evaluation.Ewma):0.000} bucket={evaluation.Bucket}";
WriteFile(Path.Combine(outputDir, "summary.txt"), summaryLine + Environment.NewLine);

Console.WriteLine("Artifacts written to {0}", outputDir);
Console.WriteLine(summaryLine);
