using System.Globalization;
using TiYf.Engine.Core.Slippage;
using TiYf.Engine.Sim;

var options = ProbeOptions.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);

var (config, _, _) = EngineConfigLoader.Load(options.ConfigPath);
var profile = config.Slippage;
var model = SlippageModelFactory.Create(profile, config.SlippageModel);
var records = LoadOrders(options.OrdersPath);

var now = DateTime.UtcNow; // used for timestamp decoration only; model is time-independent in M8-C
var results = new List<SlippageResult>();
foreach (var sample in records)
{
    var adjusted = model.Apply(sample.Price, sample.IsBuy, sample.Symbol, sample.Units, now);
    results.Add(new SlippageResult(sample, adjusted));
}

var modelName = SlippageModelFactory.Normalize(profile?.Model ?? config.SlippageModel);
var summary = new SlippageSummary(results, modelName);
WriteSummary(Path.Combine(options.OutputDirectory, "summary.txt"), summary);
WriteMetrics(Path.Combine(options.OutputDirectory, "metrics.txt"), summary);
WriteHealth(Path.Combine(options.OutputDirectory, "health.json"), summary);

return 0;

static IReadOnlyList<OrderSample> LoadOrders(string path)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException("orders file not found", path);
    }

    var lines = File.ReadAllLines(path)
        .Where(l => !string.IsNullOrWhiteSpace(l))
        .ToArray();
    if (lines.Length <= 1)
    {
        return Array.Empty<OrderSample>();
    }

    var samples = new List<OrderSample>();
    foreach (var line in lines.Skip(1))
    {
        var parts = line.Split(',');
        if (parts.Length < 4)
        {
            continue;
        }

        var symbol = parts[0].Trim();
        var sideRaw = parts[1].Trim().ToLowerInvariant();
        var isBuy = sideRaw is "buy" or "b";
        var price = decimal.Parse(parts[2], CultureInfo.InvariantCulture);
        var units = long.Parse(parts[3], CultureInfo.InvariantCulture);
        samples.Add(new OrderSample(symbol, isBuy, price, units));
    }

    return samples;
}

static void WriteSummary(string path, SlippageSummary summary)
{
    var line = $"slippage_summary model={summary.Model} orders={summary.TotalOrders} non_zero={summary.NonZeroCount} avg_delta={summary.AverageDelta:F6} last_delta={summary.LastDelta:F6}";
    File.WriteAllText(path, line);
}

static void WriteMetrics(string path, SlippageSummary summary)
{
    var builder = new System.Text.StringBuilder();
    builder.AppendLine($"engine_slippage_model{{model=\"{summary.Model}\"}} 1");
    builder.AppendLine($"slippage_probe_orders_total {summary.TotalOrders}");
    builder.AppendLine($"slippage_probe_non_zero_total {summary.NonZeroCount}");
    builder.AppendLine($"slippage_probe_distinct_symbols {summary.DistinctSymbols}");
    builder.AppendLine($"slippage_probe_price_delta_total {summary.TotalDelta.ToString(CultureInfo.InvariantCulture)}");
    File.WriteAllText(path, builder.ToString());
}

static void WriteHealth(string path, SlippageSummary summary)
{
    var payload = new
    {
        slippage_model = summary.Model,
        orders_total = summary.TotalOrders,
        non_zero_total = summary.NonZeroCount,
        last_delta = summary.LastDelta
    };
    var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(path, json);
}

internal sealed record OrderSample(string Symbol, bool IsBuy, decimal Price, long Units);

internal sealed record SlippageResult(OrderSample Sample, decimal AdjustedPrice)
{
    public decimal Delta => AdjustedPrice - Sample.Price;
}

internal sealed class SlippageSummary
{
    public SlippageSummary(IEnumerable<SlippageResult> results, string model)
    {
        Model = model;
        var list = results.ToList();
        TotalOrders = list.Count;
        TotalDelta = list.Sum(r => r.Delta);
        NonZeroCount = list.Count(r => r.Delta != 0m);
        LastDelta = list.LastOrDefault()?.Delta ?? 0m;
        DistinctSymbols = list.Select(r => r.Sample.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        AverageDelta = TotalOrders > 0 ? (double)(TotalDelta / TotalOrders) : 0d;
    }

    public string Model { get; }
    public int TotalOrders { get; }
    public int NonZeroCount { get; }
    public int DistinctSymbols { get; }
    public decimal TotalDelta { get; }
    public double AverageDelta { get; }
    public decimal LastDelta { get; }
}

internal sealed class ProbeOptions
{
    public string ConfigPath { get; }
    public string OrdersPath { get; }
    public string OutputDirectory { get; }

    private ProbeOptions(string configPath, string ordersPath, string outputDirectory)
    {
        ConfigPath = configPath;
        OrdersPath = ordersPath;
        OutputDirectory = outputDirectory;
    }

    public static ProbeOptions Parse(string[] args)
    {
        var configPath = "proof/slippage/slippage-config.json";
        var ordersPath = "proof/slippage/orders.csv";
        var outputDir = Path.Combine("artifacts", "m8-slippage-proof");

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--orders" when i + 1 < args.Length:
                    ordersPath = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    outputDir = args[++i];
                    break;
            }
        }

        return new ProbeOptions(
            Path.GetFullPath(configPath),
            Path.GetFullPath(ordersPath),
            Path.GetFullPath(outputDir));
    }
}
