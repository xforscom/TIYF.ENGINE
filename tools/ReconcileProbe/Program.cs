using System.Globalization;
using System.Text.Json;
using TiYf.Engine.Host;
using TiYf.Engine.Sim;

var argsMap = ParseArgs(args);
var configPath = argsMap.TryGetValue("--config", out var cfg) ? cfg : "proof/m8-reconcile-config.json";
var outputPath = argsMap.TryGetValue("--output", out var outDir) ? outDir : "proof-artifacts/m8-reconcile";
Directory.CreateDirectory(outputPath);

ReconcileScenario scenario;
await using (var doc = JsonDocument.Parse(File.ReadAllText(configPath)))
{
    scenario = ReconcileScenario.Parse(doc.RootElement);
}

var utcNow = scenario.UtcNow;
var records = ReconciliationRecordBuilder.Build(
    utcNow,
    scenario.EnginePositions,
    scenario.BrokerSnapshot);

var mismatches = records.Count(r => r.Status == ReconciliationStatus.Mismatch);
var unknown = records.Count(r => r.Status == ReconciliationStatus.Unknown);
var brokerOrders = scenario.BrokerSnapshot?.Orders?.Count ?? 0;
var brokerOrphans = brokerOrders > 0 && scenario.EnginePositions.Count == 0 ? brokerOrders : 0;
var summary = $"reconcile_summary mismatches={mismatches} unknown={unknown} broker_orders={brokerOrders} broker_orphans={brokerOrphans}";

var state = new EngineHostState("reconcile-proof", Array.Empty<string>());
state.MarkConnected(true);
state.RecordReconciliationTelemetry(
    (mismatches > 0 || brokerOrphans > 0) ? ReconciliationStatus.Mismatch : ReconciliationStatus.Match,
    mismatches + brokerOrphans,
    utcNow);

var metrics = EngineMetricsFormatter.Format(state.CreateMetricsSnapshot());
var health = JsonSerializer.Serialize(state.CreateHealthPayload(), new JsonSerializerOptions { WriteIndented = true });
var events = string.Join(Environment.NewLine, records.Select(r => $"{r.UtcTimestamp:o},{r.Symbol},{r.Status},{r.Reason}"));

await File.WriteAllTextAsync(Path.Combine(outputPath, "summary.txt"), summary);
await File.WriteAllTextAsync(Path.Combine(outputPath, "metrics.txt"), metrics);
await File.WriteAllTextAsync(Path.Combine(outputPath, "health.json"), health);
await File.WriteAllTextAsync(Path.Combine(outputPath, "reconcile.csv"), events);

Console.WriteLine(summary);

static Dictionary<string, string> ParseArgs(string[] raw)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < raw.Length - 1; i++)
    {
        if (!raw[i].StartsWith("--", StringComparison.Ordinal)) continue;
        var key = raw[i];
        var val = raw[i + 1];
        if (val.StartsWith("--", StringComparison.Ordinal)) throw new InvalidOperationException($"Flag {key} missing value");
        map[key] = val;
        i++;
    }
    return map;
}

internal sealed record ReconcileScenario(
    DateTime UtcNow,
    IReadOnlyList<(string Symbol, TradeSide Side, decimal EntryPrice, long Units, DateTime OpenTimestamp)> EnginePositions,
    BrokerAccountSnapshot? BrokerSnapshot)
{
    public static ReconcileScenario Parse(JsonElement root)
    {
        var utc = DateTime.Parse(root.GetProperty("utc_now").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var enginePositions = new List<(string, TradeSide, decimal, long, DateTime)>();
        if (root.TryGetProperty("engine_positions", out var engArr) && engArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in engArr.EnumerateArray())
            {
                var sym = el.GetProperty("symbol").GetString() ?? string.Empty;
                var side = ParseSide(el.GetProperty("side").GetString());
                var units = el.GetProperty("units").GetInt64();
                var price = el.GetProperty("entry_price").GetDecimal();
                var open = DateTime.Parse(el.GetProperty("open_utc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                enginePositions.Add((sym, side, price, units, open));
            }
        }

        BrokerAccountSnapshot? broker = null;
        if (root.TryGetProperty("broker", out var brokerNode) && brokerNode.ValueKind == JsonValueKind.Object)
        {
            var positions = new List<BrokerPositionSnapshot>();
            if (brokerNode.TryGetProperty("positions", out var posArr) && posArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in posArr.EnumerateArray())
                {
                    positions.Add(new BrokerPositionSnapshot(
                        el.GetProperty("symbol").GetString() ?? string.Empty,
                        ParseSide(el.GetProperty("side").GetString()),
                        el.GetProperty("units").GetInt64(),
                        el.TryGetProperty("avg_price", out var avg) && avg.ValueKind == JsonValueKind.Number
                            ? avg.GetDecimal()
                            : null));
                }
            }
            var orders = new List<BrokerOrderSnapshot>();
            if (brokerNode.TryGetProperty("orders", out var ordArr) && ordArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in ordArr.EnumerateArray())
                {
                    orders.Add(new BrokerOrderSnapshot(
                        el.GetProperty("broker_order_id").GetString() ?? string.Empty,
                        el.GetProperty("symbol").GetString() ?? string.Empty,
                        ParseSide(el.GetProperty("side").GetString()),
                        el.GetProperty("units").GetInt64(),
                        el.TryGetProperty("price", out var priceNode) && priceNode.ValueKind == JsonValueKind.Number ? priceNode.GetDecimal() : null,
                        el.GetProperty("status").GetString() ?? string.Empty));
                }
            }
            broker = new BrokerAccountSnapshot(utc, positions, orders);
        }

        return new ReconcileScenario(utc, enginePositions, broker);
    }

    private static TradeSide ParseSide(string? raw)
    {
        return string.Equals(raw, "sell", StringComparison.OrdinalIgnoreCase) ? TradeSide.Sell : TradeSide.Buy;
    }
}
