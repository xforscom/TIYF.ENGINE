using System.Globalization;
using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Host;
using TiYf.Engine.Sim;

var argsMap = ParseArgs(args);
var configPath = argsMap.TryGetValue("--config", out var config) ? config : "proof/m10-gvrs-config.json";
var outputPath = argsMap.TryGetValue("--output", out var output) ? output : "proof-artifacts/m10-gvrs-gate";
var nowOverride = argsMap.TryGetValue("--now", out var nowRaw) ? nowRaw : null;

var (engineConfig, configHash, raw) = EngineConfigLoader.Load(configPath);
using var rawDoc = raw;
var nowUtc = ParseUtc(nowOverride) ?? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
Directory.CreateDirectory(outputPath);

var state = new EngineHostState("gvrs-gate-proof", Array.Empty<string>());
state.MarkConnected(true);
state.SetLoopStart(nowUtc);
state.SetConfigSource(Path.GetFullPath(configPath), configHash);
var gateConfig = GvrsGateConfigHelper.Resolve(rawDoc);
state.SetGvrsGateConfig(gateConfig.Enabled, gateConfig.BlockOnVolatile);
state.SetGvrsSnapshot(new MarketContextService.GvrsSnapshot(0.9m, 0.85m, "volatile", "shadow", true));
if (gateConfig.Enabled)
{
    state.RegisterGvrsGateBlock(nowUtc);
}

var snapshot = state.CreateMetricsSnapshot();
var metrics = EngineMetricsFormatter.Format(snapshot);
var health = JsonSerializer.Serialize(state.CreateHealthPayload(), new JsonSerializerOptions { WriteIndented = true });
var bucket = snapshot.GvrsBucket ?? "Unknown";
var blocksTotal = snapshot.GvrsGateBlocksTotal;
var lastBlock = snapshot.GvrsGateLastBlockUnixSeconds.HasValue
    ? DateTimeOffset.FromUnixTimeSeconds((long)snapshot.GvrsGateLastBlockUnixSeconds.Value).UtcDateTime.ToString("O")
    : "n/a";
var summary = $"gvrs_gate_summary bucket={bucket} gate_enabled={gateConfig.Enabled.ToString().ToLowerInvariant()} blocking_enabled={gateConfig.BlockOnVolatile.ToString().ToLowerInvariant()} blocks_total={blocksTotal} last_block_utc={lastBlock}";

await File.WriteAllTextAsync(Path.Combine(outputPath, "metrics.txt"), metrics);
await File.WriteAllTextAsync(Path.Combine(outputPath, "health.json"), health);
await File.WriteAllTextAsync(Path.Combine(outputPath, "summary.txt"), summary);

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
            continue;
        }
        map[key] = value;
        i++;
    }
    return map;
}

static DateTime? ParseUtc(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }

    if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
    {
        return parsed.Kind == DateTimeKind.Utc ? parsed : DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    return null;
}
