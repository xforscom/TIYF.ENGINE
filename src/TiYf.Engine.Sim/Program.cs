using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Sidecar;
using TiYf.Engine.Sim;

const string EngineInstanceId = "engine-local-1"; // could be GUID in future

// Basic CLI harness: dotnet run --project src/TiYf.Engine.Sim -- --config sample-config.json

string? configPath = null;
string? outPath = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--config" && i + 1 < args.Length)
        configPath = args[i + 1];
    if (args[i] == "--out" && i + 1 < args.Length)
        outPath = args[i + 1];
}

configPath ??= "sample-config.json";
var fullConfigPath = Path.GetFullPath(configPath);

try
{
    if (!File.Exists(fullConfigPath))
    {
        Console.WriteLine($"Config '{fullConfigPath}' not found. Creating sample.");
    var sample = "{\n  \"SchemaVersion\":\"" + TiYf.Engine.Core.Infrastructure.Schema.Version + "\",\n  \"RunId\":\"RUN-DEMO\",\n  \"InstrumentFile\":\"sample-instruments.csv\",\n  \"InputTicksFile\":\"sample-ticks.csv\",\n  \"JournalRoot\":\"journals\"\n}";
        await File.WriteAllTextAsync(fullConfigPath, sample);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to create config: {ex.Message}");
    return;
}

// Ensure sample data
SampleDataSeeder.EnsureSample(Directory.GetCurrentDirectory());

var (cfg, cfgHash, raw) = EngineConfigLoader.Load(fullConfigPath);
Console.WriteLine($"Loaded config RunId={cfg.RunId} hash={cfgHash}");

// Load instruments (simplified: single instrument)
var instrumentLines = File.ReadAllLines(cfg.InstrumentFile);
var instrument = new Instrument(new InstrumentId("INST1"), "FOO", 2);
var catalog = new InMemoryInstrumentCatalog(new[] { instrument });

// Build clock from tick timestamps (sequence mode) by reading tick file first
var sequence = new List<DateTime>();
foreach (var line in File.ReadLines(cfg.InputTicksFile).Skip(1))
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    var parts = line.Split(',');
    sequence.Add(DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal));
}
var clock = new DeterministicSequenceClock(sequence);

// Tick source
var tickSource = new CsvTickSource(cfg.InputTicksFile, instrument.Id);

// Determine instrument set
var instrumentIds = (cfg.Instruments is { Length: >0 }
    ? cfg.Instruments
    : new[] { instrument.Id.Value })
    .Select(s => new InstrumentId(s))
    .Distinct()
    .ToList();

// Determine intervals
BarInterval MapInterval(string code) => code.ToUpperInvariant() switch
{
    "M1" => BarInterval.OneMinute,
    "H1" => BarInterval.OneHour,
    "D1" => BarInterval.OneDay,
    _ => throw new ArgumentException($"Unsupported interval {code}")
};
var intervals = (cfg.Intervals is { Length: >0 } ? cfg.Intervals : new[] { "M1" })
    .Select(MapInterval)
    .Distinct()
    .ToList();

// Build multi (instrument, interval) builders
var builders = new Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder>();
foreach (var iid in instrumentIds)
    foreach (var ivl in intervals)
        builders[(iid, ivl)] = new IntervalBarBuilder(ivl);

// Load bar key snapshot if present
var snapshotPath = Path.Combine(cfg.JournalRoot, cfg.RunId, "bar-keys.snapshot.json");
var barKeyTracker = BarKeyTrackerPersistence.Load(snapshotPath);

// Compute optional data_version for backtest-m0 fixture (detect by config name or presence of ticks files path pattern)
string? dataVersion = null;
try
{
    if (raw.RootElement.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String && nameEl.GetString() == "backtest-m0")
    {
        var rootProp = raw.RootElement.GetProperty("data").GetProperty("ticks");
        var paths = new List<string>();
        if (raw.RootElement.GetProperty("data").TryGetProperty("instrumentsFile", out var instEl) && instEl.ValueKind==JsonValueKind.String)
            paths.Add(instEl.GetString()!);
        foreach (var p in rootProp.EnumerateObject()) if (p.Value.ValueKind==JsonValueKind.String) paths.Add(p.Value.GetString()!);
        // Also include config file itself if we can resolve path (cfg.ConfigPath if available else skip)
        if (!string.IsNullOrWhiteSpace(configPath)) paths.Add(configPath);
        // Normalize to repo-relative existing paths only
        var existing = paths.Where(File.Exists).ToArray();
        if (existing.Length>0)
            dataVersion = TiYf.Engine.Core.DataVersion.Compute(existing);
    }
}
catch { /* Non-fatal; omit data_version if any parsing fails */ }

// Journal writer with optional data_version
await using var journal = new FileJournalWriter(cfg.JournalRoot, cfg.RunId, cfg.SchemaVersion, cfgHash, dataVersion);

// Extract risk config + equity from raw JSON (tolerant: defaults if missing)
decimal equity = 100_000m; // fallback
RiskConfig riskConfig = new();
try
{
    if (raw.RootElement.TryGetProperty("equity", out var eqEl) && eqEl.ValueKind == JsonValueKind.Number) equity = eqEl.GetDecimal();
    if (raw.RootElement.TryGetProperty("risk", out var riskEl) && riskEl.ValueKind == JsonValueKind.Object)
    {
        decimal TryNum(string name, decimal fallback) => riskEl.TryGetProperty(name, out var v) && v.ValueKind==JsonValueKind.Number ? v.GetDecimal() : fallback;
        bool TryBool(string name, bool fallback) => riskEl.TryGetProperty(name, out var v) && v.ValueKind==JsonValueKind.True || v.ValueKind==JsonValueKind.False ? v.GetBoolean() : fallback;
        string TryStr(string name, string fallback) => riskEl.TryGetProperty(name, out var v) && v.ValueKind==JsonValueKind.String ? v.GetString() ?? fallback : fallback;
        var buckets = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if (riskEl.TryGetProperty("instrument_buckets", out var bEl) && bEl.ValueKind==JsonValueKind.Object)
        {
            foreach (var p in bEl.EnumerateObject()) if (p.Value.ValueKind==JsonValueKind.String) buckets[p.Name] = p.Value.GetString() ?? string.Empty;
        }
        riskConfig = new RiskConfig
        {
            RealLeverageCap = TryNum("real_leverage_cap", 20m),
            MarginUsageCapPct = TryNum("margin_usage_cap_pct", 80m),
            PerPositionRiskCapPct = TryNum("per_position_risk_cap_pct", 1m),
            BasketMode = TryStr("basket_mode", "Base"),
            InstrumentBuckets = buckets,
            EnableScaleToFit = TryBool("enable_scale_to_fit", false),
            EnforcementEnabled = TryBool("enforcement_enabled", true),
            LotStep = TryNum("lot_step", 0.01m)
        };
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Warning: failed to parse risk config, using defaults. {ex.Message}");
}

var riskFormulas = new RiskFormulas();
var basketAgg = new BasketRiskAggregator();
var enforcer = new RiskEnforcer(riskFormulas, basketAgg, cfg.SchemaVersion, cfgHash);

var loop = new EngineLoop(clock, builders, barKeyTracker, journal, tickSource, cfg.BarOutputEventType, () =>
{
    // Persist after each emitted bar (simple, can batch later)
    BarKeyTrackerPersistence.Save(snapshotPath, (InMemoryBarKeyTracker)barKeyTracker, cfg.SchemaVersion, EngineInstanceId);
}, riskFormulas, basketAgg, cfgHash, cfg.SchemaVersion, enforcer, riskConfig, equity)
{
    // future injection points if needed
};
await loop.RunAsync();

Console.WriteLine("Engine run complete.");

// If --out provided, copy the produced journal events file to that path for deterministic tooling
if (!string.IsNullOrWhiteSpace(outPath))
{
    try
    {
        var sourceEvents = Path.Combine(cfg.JournalRoot, cfg.RunId, "events.csv");
        if (!File.Exists(sourceEvents))
        {
            Console.Error.WriteLine($"Expected journal file not found at {sourceEvents}");
        }
        else
        {
            var destFull = Path.GetFullPath(outPath);
            var destDir = Path.GetDirectoryName(destFull);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);
            File.Copy(sourceEvents, destFull, true);
            Console.WriteLine($"Copied journal to {destFull}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to copy journal to --out path: {ex.Message}");
    }
}