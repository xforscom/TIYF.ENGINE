using System.Globalization;
using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Core.Infrastructure;
using TiYf.Engine.Host;
using TiYf.Engine.Sim;

// RiskRailsProbe drives the m11-risk-rails proof workflow: it loads a deterministic config,
// replays synthetic trades, and emits summary/metrics/health artifacts for validation.
var argsMap = ParseArgs(args);
var configPath = argsMap.TryGetValue("--config", out var config) ? config : "proof/m11-risk-config.json";
var outputPath = argsMap.TryGetValue("--output", out var output) ? output : "proof-artifacts/m11-risk-rails";
Directory.CreateDirectory(outputPath);

using var configDoc = JsonDocument.Parse(File.ReadAllText(configPath));
var root = configDoc.RootElement;
if (!root.TryGetProperty("risk", out var riskNode) || riskNode.ValueKind != JsonValueKind.Object)
{
    throw new InvalidOperationException("risk block is required in the proof config");
}
if (!root.TryGetProperty("scenario", out var scenarioNode) || scenarioNode.ValueKind != JsonValueKind.Object)
{
    throw new InvalidOperationException("scenario block is required in the proof config");
}

var riskConfig = RiskConfigParser.Parse(riskNode);
var canonical = JsonCanonicalizer.Canonicalize(riskNode);
var riskConfigHash = riskConfig.RiskConfigHash ?? ConfigHash.Compute(canonical);
var scenario = Scenario.Parse(scenarioNode);

RiskRailTelemetrySnapshot? telemetrySnapshot = null;
var gateCounts = new Dictionary<string, (int blocks, int throttles)>(StringComparer.OrdinalIgnoreCase);
var runtime = new RiskRailRuntime(
    riskConfig,
    riskConfigHash,
    Array.Empty<NewsEvent>(),
    gateCallback: (gate, throttled) =>
    {
        if (string.IsNullOrWhiteSpace(gate))
        {
            return;
        }
        var current = gateCounts.TryGetValue(gate, out var counts) ? counts : (0, 0);
        gateCounts[gate] = throttled ? (current.Item1, current.Item2 + 1) : (current.Item1 + 1, current.Item2);
    },
    scenario.StartingEquity,
    telemetryCallback: snapshot => telemetrySnapshot = snapshot,
    clock: () => scenario.DecisionUtc);

var tracker = new PositionTracker();
ApplyTrades(tracker, scenario.Trades);
var bar = new Bar(
    new InstrumentId(scenario.Instrument),
    scenario.DecisionUtc.AddMinutes(-1),
    scenario.DecisionUtc,
    scenario.BarPrice,
    scenario.BarPrice,
    scenario.BarPrice,
    scenario.BarPrice,
    1m);
runtime.UpdateBar(bar, tracker);

var openPositions = scenario.OpenPositions.Count == 0
    ? Array.Empty<RiskPositionUnits>()
    : scenario.OpenPositions.ToArray();
var outcome = runtime.EvaluateNewEntry(
    scenario.Instrument,
    scenario.Timeframe,
    scenario.DecisionUtc,
    scenario.RequestedUnits,
    openPositions);

var telemetry = telemetrySnapshot ?? new RiskRailTelemetrySnapshot(
    null,
    0m,
    0,
    null,
    0,
    0,
    null,
    new Dictionary<string, long>(),
    new Dictionary<string, long>(),
    false,
    false,
    null,
    0,
    null,
    null);

var summary = BuildSummary(outcome, telemetry);
var state = new EngineHostState("risk-rails-proof", Array.Empty<string>());
state.MarkConnected(true);
state.SetLoopStart(scenario.DecisionUtc);
state.SetConfigSource(Path.GetFullPath(configPath), riskConfigHash);
state.SetRiskConfigHash(riskConfigHash);
state.UpdateRiskRailsTelemetry(telemetry);
foreach (var kvp in gateCounts)
{
    for (var i = 0; i < kvp.Value.blocks; i++)
    {
        state.RegisterRiskGateEvent(kvp.Key, throttled: false);
    }
    for (var i = 0; i < kvp.Value.throttles; i++)
    {
        state.RegisterRiskGateEvent(kvp.Key, throttled: true);
    }
}
var metrics = EngineMetricsFormatter.Format(state.CreateMetricsSnapshot());
var health = JsonSerializer.Serialize(state.CreateHealthPayload(), new JsonSerializerOptions { WriteIndented = true });
var events = string.Join(Environment.NewLine, outcome.Alerts.Select(a => a.EventType));

await File.WriteAllTextAsync(Path.Combine(outputPath, "summary.txt"), summary);
await File.WriteAllTextAsync(Path.Combine(outputPath, "metrics.txt"), metrics);
await File.WriteAllTextAsync(Path.Combine(outputPath, "health.json"), health);
await File.WriteAllTextAsync(Path.Combine(outputPath, "events.csv"), events);

Console.WriteLine(summary);

static Dictionary<string, string> ParseArgs(string[] rawArgs)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < rawArgs.Length - 1; i++)
    {
        var key = rawArgs[i];
        if (!key.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }
        var value = rawArgs[i + 1];
        if (value.StartsWith("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Flag '{key}' requires a value.");
        }
        map[key] = value;
        i++;
    }
    return map;
}

static void ApplyTrades(PositionTracker tracker, IReadOnlyList<ScenarioTrade> trades)
{
    foreach (var trade in trades)
    {
        tracker.OnFill(
            new ExecutionFill(trade.DecisionId, trade.Symbol, trade.Side, trade.EntryPrice, trade.Units, trade.OpenUtc),
            Schema.Version,
            trade.ConfigHash,
            "risk-rails-proof",
            null);
        var exitSide = trade.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
        tracker.OnFill(
            new ExecutionFill(trade.DecisionId, trade.Symbol, exitSide, trade.ExitPrice, trade.Units, trade.CloseUtc),
            Schema.Version,
            trade.ConfigHash,
            "risk-rails-proof",
            null);
    }
}

static string BuildSummary(RiskRailOutcome outcome, RiskRailTelemetrySnapshot telemetry)
{
    var brokerCap = outcome.Alerts.Any(a => a.EventType == "ALERT_RISK_BROKER_DAILY_CAP_SOFT") ? 1 : 0;
    var maxPosition = outcome.Alerts.Any(a => a.EventType == "ALERT_RISK_MAX_POSITION_SOFT") ? 1 : 0;
    var symbolCaps = outcome.Alerts.Count(a => a.EventType == "ALERT_RISK_SYMBOL_CAP_SOFT");
    var cooldownAlert = outcome.Alerts.Any(a => a.EventType == "ALERT_RISK_COOLDOWN_SOFT") ? 1 : 0;
    var hardBlocks = outcome.Alerts.Count(a => a.EventType.EndsWith("_HARD", StringComparison.OrdinalIgnoreCase));
    return $"risk_rails_summary allowed={outcome.Allowed} broker_cap_hit={brokerCap} max_position_hit={maxPosition} symbol_caps_hit={symbolCaps} cooldown_alert={cooldownAlert} hard_blocks={hardBlocks} cooldown_triggers={telemetry.CooldownTriggersTotal}";
}

internal sealed record Scenario(
    string Instrument,
    string Timeframe,
    DateTime DecisionUtc,
    long RequestedUnits,
    decimal BarPrice,
    decimal StartingEquity,
    IReadOnlyList<ScenarioTrade> Trades,
    IReadOnlyList<RiskPositionUnits> OpenPositions)
{
    public static Scenario Parse(JsonElement node)
    {
        var instrument = GetRequiredString(node, "instrument");
        var timeframe = GetRequiredString(node, "timeframe");
        var decisionUtc = ParseUtc(GetRequiredString(node, "decision_utc"));
        var requestedUnits = GetRequiredLong(node, "requested_units");
        var barPrice = node.TryGetProperty("bar_price", out var barEl) && barEl.ValueKind == JsonValueKind.Number ? barEl.GetDecimal() : 1m;
        var startingEquity = node.TryGetProperty("starting_equity", out var equityEl) && equityEl.ValueKind == JsonValueKind.Number ? equityEl.GetDecimal() : 100_000m;
        var trades = ParseTrades(node);
        var openPositions = ParseOpenPositions(node);
        return new Scenario(instrument, timeframe, decisionUtc, requestedUnits, barPrice, startingEquity, trades, openPositions);
    }

    private static IReadOnlyList<ScenarioTrade> ParseTrades(JsonElement node)
    {
        if (!node.TryGetProperty("trades", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ScenarioTrade>();
        }

        var trades = new List<ScenarioTrade>();
        foreach (var item in arr.EnumerateArray())
        {
            var id = GetRequiredString(item, "decision_id");
            var symbol = GetRequiredString(item, "symbol");
            var side = ParseSide(GetRequiredString(item, "side"));
            var units = GetRequiredLong(item, "units");
            var entryPrice = item.GetProperty("entry_price").GetDecimal();
            var exitPrice = item.GetProperty("exit_price").GetDecimal();
            var openUtc = ParseUtc(GetRequiredString(item, "open_utc"));
            var closeUtc = ParseUtc(GetRequiredString(item, "close_utc"));
            trades.Add(new ScenarioTrade(id, symbol, side, units, entryPrice, exitPrice, openUtc, closeUtc));
        }

        return trades;
    }

    private static IReadOnlyList<RiskPositionUnits> ParseOpenPositions(JsonElement node)
    {
        if (!node.TryGetProperty("open_positions", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<RiskPositionUnits>();
        }

        var positions = new List<RiskPositionUnits>();
        foreach (var item in arr.EnumerateArray())
        {
            if (!item.TryGetProperty("symbol", out var symbolEl) || symbolEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            if (!item.TryGetProperty("units", out var unitsEl) || unitsEl.ValueKind != JsonValueKind.Number)
            {
                continue;
            }
            var symbol = symbolEl.GetString();
            var units = unitsEl.GetInt64();
            if (string.IsNullOrWhiteSpace(symbol) || units <= 0)
            {
                continue;
            }
            positions.Add(new RiskPositionUnits(symbol, units));
        }

        return positions;
    }

    private static string GetRequiredString(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"scenario.{property} must be provided");
        }
        var value = el.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"scenario.{property} must not be empty");
        }
        return value.Trim();
    }

    private static long GetRequiredLong(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException($"scenario.{property} must be provided");
        }
        return el.GetInt64();
    }

    private static DateTime ParseUtc(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.Kind == DateTimeKind.Utc ? parsed : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }
        throw new InvalidOperationException($"Invalid UTC timestamp '{value}'");
    }

    private static TradeSide ParseSide(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "buy" => TradeSide.Buy,
            "sell" => TradeSide.Sell,
            _ => throw new InvalidOperationException($"Unsupported trade side '{raw}'")
        };
    }
}

internal sealed record ScenarioTrade(
    string DecisionId,
    string Symbol,
    TradeSide Side,
    long Units,
    decimal EntryPrice,
    decimal ExitPrice,
    DateTime OpenUtc,
    DateTime CloseUtc,
    string ConfigHash = "proof")
{
    public ScenarioTrade(string decisionId, string symbol, TradeSide side, long units, decimal entryPrice, decimal exitPrice, DateTime openUtc, DateTime closeUtc)
        : this(decisionId, symbol, side, units, entryPrice, exitPrice, openUtc, closeUtc, "proof")
    {
    }
}
