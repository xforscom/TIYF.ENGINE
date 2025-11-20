using System.Globalization;
using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Host;
using TiYf.Engine.Sim;
using TiYf.Engine.Core.Infrastructure;

var options = ParseArgs(args);
try
{
    RunProbe(options);
    Console.WriteLine($"Promotion proof artifacts written to '{options.OutputDirectory}'");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"promotion probe failed: {ex.Message}");
    Environment.Exit(1);
}

static void RunProbe(CliOptions options)
{
    var configPath = Path.GetFullPath(options.ConfigPath);
    if (!File.Exists(configPath))
    {
        throw new FileNotFoundException($"Config file not found: {configPath}");
    }

    var outputDir = Path.GetFullPath(options.OutputDirectory);
    Directory.CreateDirectory(outputDir);

    using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
    if (!doc.RootElement.TryGetProperty("risk", out var riskEl))
    {
        throw new InvalidOperationException("Config missing 'risk' section.");
    }

    var riskConfig = RiskConfigParser.Parse(riskEl);
    PromotionShadowSnapshot? shadowSnapshot = null;

    if (doc.RootElement.TryGetProperty("scenario", out var scenarioEl))
    {
        shadowSnapshot = RunScenario(riskConfig, scenarioEl);
    }

    WriteHealth(Path.Combine(outputDir, "health.json"), riskConfig, shadowSnapshot);
    WriteMetrics(Path.Combine(outputDir, "metrics.txt"), riskConfig, shadowSnapshot);
    WriteSummary(Path.Combine(outputDir, "summary.txt"), configPath, riskConfig.Promotion, shadowSnapshot);
}

static PromotionShadowSnapshot? RunScenario(RiskConfig riskConfig, JsonElement scenarioEl)
{
    var runtime = new PromotionShadowRuntime(riskConfig.Promotion);
    var tracker = new PositionTracker();
    DateTime evaluationUtc = DateTime.UtcNow;
    DateTime? latestClose = null;

    if (scenarioEl.TryGetProperty("trades", out var tradesEl) && tradesEl.ValueKind == JsonValueKind.Array)
    {
        foreach (var trade in tradesEl.EnumerateArray())
        {
            var symbol = trade.GetProperty("symbol").GetString() ?? "EURUSD";
            var side = ParseSide(trade.GetProperty("side").GetString() ?? "buy");
            var units = trade.GetProperty("units").GetInt64();
            var entry = trade.GetProperty("entry_price").GetDecimal();
            var exit = trade.GetProperty("exit_price").GetDecimal();
            var openUtc = ParseUtc(trade.GetProperty("open_utc").GetString() ?? throw new InvalidOperationException("open_utc required"));
            var closeUtc = ParseUtc(trade.GetProperty("close_utc").GetString() ?? throw new InvalidOperationException("close_utc required"));
            if (!latestClose.HasValue || closeUtc > latestClose.Value)
            {
                latestClose = closeUtc;
            }
            var decisionId = trade.GetProperty("decision_id").GetString() ?? Guid.NewGuid().ToString("N");
            tracker.OnFill(new ExecutionFill(decisionId, symbol, side, entry, units, openUtc), Schema.Version, riskConfig.RiskConfigHash ?? string.Empty, "promotion-probe", null);
            var exitSide = side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
            tracker.OnFill(new ExecutionFill(decisionId, symbol, exitSide, exit, units, closeUtc), Schema.Version, riskConfig.RiskConfigHash ?? string.Empty, "promotion-probe", null);
        }
    }

    if (latestClose.HasValue)
    {
        evaluationUtc = latestClose.Value;
    }

    return runtime.Evaluate(tracker, evaluationUtc);
}

static TradeSide ParseSide(string value)
{
    return value.Equals("buy", StringComparison.OrdinalIgnoreCase) ? TradeSide.Buy : TradeSide.Sell;
}

static DateTime ParseUtc(string value)
{
    if (!DateTime.TryParse(value, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
    {
        throw new InvalidOperationException($"Invalid UTC timestamp: {value}");
    }
    return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
}

static void WriteHealth(string path, RiskConfig riskConfig, PromotionShadowSnapshot? shadow)
{
    var payload = new
    {
        promotion_config_hash = riskConfig.PromotionConfigHash,
        promotion = BuildPromotionBlock(riskConfig.Promotion, shadow)
    };

    var options = new JsonSerializerOptions { WriteIndented = true };
    var json = JsonSerializer.Serialize(payload, options);
    File.WriteAllText(path, json + Environment.NewLine);
}

static void WriteMetrics(string path, RiskConfig riskConfig, PromotionShadowSnapshot? shadow)
{
    var state = new EngineHostState("promotion-probe", Array.Empty<string>());
    state.SetRiskConfigHash(riskConfig.RiskConfigHash ?? string.Empty);
    state.SetPromotionConfig(riskConfig.Promotion);
    if (shadow is not null)
    {
        state.UpdatePromotionShadow(shadow);
    }
    var snapshot = state.CreateMetricsSnapshot();
    var metrics = EngineMetricsFormatter.Format(snapshot);
    File.WriteAllText(path, metrics);
}

static void WriteSummary(string path, string configPath, PromotionConfig promotion, PromotionShadowSnapshot? shadow)
{
    var hasPromotion = promotion is not null && !string.IsNullOrWhiteSpace(promotion.ConfigHash);
    var shadowCandidates = promotion?.ShadowCandidates ?? Array.Empty<string>();
    var candidateCount = hasPromotion ? shadowCandidates.Count : 0;
    var probationDays = hasPromotion ? promotion!.ProbationDays : (int?)null;
    var minTrades = hasPromotion ? promotion!.MinTrades : (int?)null;
    var promotionThreshold = hasPromotion ? promotion!.PromotionThreshold : (decimal?)null;
    var demotionThreshold = hasPromotion ? promotion!.DemotionThreshold : (decimal?)null;
    var candidatesList = hasPromotion ? string.Join(',', shadowCandidates) : "n/a";
    var line = string.Format(
        CultureInfo.InvariantCulture,
        "promotion-proof: config={0} hash={1} promotion_candidates={2} probation_days={3} min_trades={4} promotion_threshold={5} demotion_threshold={6} candidates=[{7}] promotions_total={8} demotions_total={9} trades_total={10} win_ratio={11:0.###}",
        Path.GetFileName(configPath),
        hasPromotion ? promotion!.ConfigHash : "n/a",
        hasPromotion ? candidateCount.ToString(CultureInfo.InvariantCulture) : "n/a",
        probationDays?.ToString(CultureInfo.InvariantCulture) ?? "n/a",
        minTrades?.ToString(CultureInfo.InvariantCulture) ?? "n/a",
        FormatDecimal(promotionThreshold),
        FormatDecimal(demotionThreshold),
        candidatesList,
        shadow?.PromotionsTotal ?? 0,
        shadow?.DemotionsTotal ?? 0,
        shadow?.TradeCount ?? 0,
        shadow?.WinRatio ?? 0m);

    File.WriteAllText(path, line + Environment.NewLine);
}

static object? BuildPromotionBlock(PromotionConfig promotion, PromotionShadowSnapshot? shadow)
{
    if (promotion is null || string.IsNullOrWhiteSpace(promotion.ConfigHash))
    {
        return null;
    }

    return new
    {
        candidates = promotion.ShadowCandidates.ToArray(),
        probation_days = promotion.ProbationDays,
        min_trades = promotion.MinTrades,
        promotion_threshold = promotion.PromotionThreshold,
        demotion_threshold = promotion.DemotionThreshold,
        shadow = shadow is null
            ? null
            : new
            {
                promotions_total = shadow.PromotionsTotal,
                demotions_total = shadow.DemotionsTotal,
                trades_total = shadow.TradeCount,
                win_ratio = shadow.WinRatio
            }
    };
}

static string FormatDecimal(decimal? value)
{
    return value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";
}

static CliOptions ParseArgs(string[] args)
{
    var configPath = "sample-config.demo.json";
    var outputDir = Path.Combine(Environment.CurrentDirectory, "promotion-proof-artifacts");

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--config":
            case "-c":
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing value for --config");
                }
                configPath = args[++i];
                break;
            case "--output":
            case "-o":
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing value for --output");
                }
                outputDir = args[++i];
                break;
            case "--help":
            case "-h":
                PrintHelp();
                Environment.Exit(0);
                break;
            default:
                throw new ArgumentException($"Unknown argument '{args[i]}'");
        }
    }

    return new CliOptions(configPath, outputDir);
}

static void PrintHelp()
{
    Console.WriteLine("Usage: dotnet run --project tools/PromotionProbe -- [--config <path>] [--output <dir>]");
    Console.WriteLine("Defaults: config=sample-config.demo.json, output=./promotion-proof-artifacts");
}

internal readonly record struct CliOptions(string ConfigPath, string OutputDirectory);
