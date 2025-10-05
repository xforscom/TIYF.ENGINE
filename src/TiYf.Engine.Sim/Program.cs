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

Instrument instrument = new Instrument(new InstrumentId("INST1"), "FOO", 2); // legacy fallback (non-M0)
var catalog = new InMemoryInstrumentCatalog(new[] { instrument });
List<Instrument> m0Instruments = new();
bool isM0 = raw.RootElement.TryGetProperty("name", out var nameNode) && nameNode.GetString()=="backtest-m0";
string? m0JournalDir = null;
if (isM0)
{
    try
    {
        var dataNode = raw.RootElement.GetProperty("data");
        var instFile = dataNode.GetProperty("instrumentsFile").GetString();
        if (string.IsNullOrWhiteSpace(instFile)) throw new Exception("instrumentsFile path missing");
        var specs = TiYf.Engine.Core.Instruments.InstrumentsCsvLoader.Load(instFile!);
        foreach (var s in specs)
            m0Instruments.Add(new Instrument(new InstrumentId(s.Symbol), s.Symbol, s.PriceDecimals));
        catalog = new InMemoryInstrumentCatalog(m0Instruments);
        if (raw.RootElement.TryGetProperty("output", out var outNode) && outNode.TryGetProperty("journalDir", out var jd) && jd.ValueKind==JsonValueKind.String)
            m0JournalDir = jd.GetString();
    }
    catch (Exception ex) { Console.Error.WriteLine($"M0 instrument parse error: {ex.Message}"); }
}

var sequence = new List<DateTime>();
if (isM0)
{
    try
    {
        var tickObj = raw.RootElement.GetProperty("data").GetProperty("ticks");
        var allTs = new HashSet<DateTime>();
        foreach (var entry in tickObj.EnumerateObject())
        {
            var path = entry.Value.GetString();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length < 4) continue;
                var ts = DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.AssumeUniversal|System.Globalization.DateTimeStyles.AdjustToUniversal);
                allTs.Add(ts);
            }
        }
        sequence = allTs.OrderBy(t=>t).ToList();
    }
    catch (Exception ex) { Console.Error.WriteLine($"M0 tick aggregation failed: {ex.Message}"); }
}
else
{
    foreach (var line in File.ReadLines(cfg.InputTicksFile).Skip(1))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        var parts = line.Split(',');
        sequence.Add(DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal));
    }
}
var clock = new DeterministicSequenceClock(sequence);

ITickSource tickSource = isM0 ? new MultiInstrumentTickSource(raw) : new CsvTickSource(cfg.InputTicksFile, instrument.Id);

// Determine instrument set
List<InstrumentId> instrumentIds = isM0
    ? catalog.All().Select(i=>i.Id).ToList()
    : (cfg.Instruments is { Length: >0 } ? cfg.Instruments.Select(s=> new InstrumentId(s)).Distinct().ToList() : new List<InstrumentId>{instrument.Id});

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

// (Snapshot path will be resolved after journal root & run id are finalized for M0)
IBarKeyTracker? barKeyTracker = null;

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
    // Intentionally exclude the config file itself so data_version reflects ONLY raw market data fixtures (stable across config param tweaks)
        // Normalize to repo-relative existing paths only
        var existing = paths.Where(File.Exists).ToArray();
        if (existing.Length>0)
            dataVersion = TiYf.Engine.Core.DataVersion.Compute(existing);
    }
}
catch { /* Non-fatal; omit data_version if any parsing fails */ }

// Determine run id fallback for M0 (fallback journal dir)
var runId = (string.IsNullOrWhiteSpace(cfg.RunId) && isM0) ? "M0-RUN" : (cfg.RunId ?? "RUN-M0");
var journalRoot = isM0 && !string.IsNullOrWhiteSpace(m0JournalDir) ? m0JournalDir : (cfg.JournalRoot ?? "journals/M0");
// For M0 determinism, ensure a clean run directory each invocation (avoid stale appended events creating false alerts)
if (isM0)
{
    var runDir = Path.Combine(journalRoot, runId);
    if (Directory.Exists(runDir))
    {
        try { Directory.Delete(runDir, true); } catch { /* best effort */ }
    }
}
// Journal writer with optional data_version
await using var journal = new FileJournalWriter(journalRoot, runId, cfg.SchemaVersion ?? TiYf.Engine.Core.Infrastructure.Schema.Version, cfgHash, dataVersion);
// Load snapshot now that paths are final
var snapshotPath = Path.Combine(journalRoot, runId, "bar-keys.snapshot.json");
barKeyTracker = BarKeyTrackerPersistence.Load(snapshotPath);
TradesJournalWriter? tradesWriter = null; PositionTracker? positions = null; IExecutionAdapter? execution = null; TickBook? bookRef = null;
if (raw.RootElement.TryGetProperty("name", out var nmEl) && nmEl.ValueKind==JsonValueKind.String && nmEl.GetString()=="backtest-m0")
{
    positions = new PositionTracker();
    tradesWriter = new TradesJournalWriter(journalRoot, runId, cfg.SchemaVersion ?? TiYf.Engine.Core.Infrastructure.Schema.Version, cfgHash, dataVersion);
    // Build multi-instrument tick book from fixture files if present
    try
    {
        if (raw.RootElement.TryGetProperty("data", out var dataNode) && dataNode.TryGetProperty("ticks", out var ticksNode) && ticksNode.ValueKind==JsonValueKind.Object)
        {
            var rows = new List<(string Symbol, DateTime Ts, decimal Bid, decimal Ask)>();
            foreach (var tkv in ticksNode.EnumerateObject())
            {
                var sym = tkv.Name;
                var path = tkv.Value.GetString();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path).Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    if (parts.Length < 4) continue; // timestamp,bid,ask,volume
                    var ts = DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.AssumeUniversal|System.Globalization.DateTimeStyles.AdjustToUniversal);
                    var bid = decimal.Parse(parts[1]);
                    var ask = decimal.Parse(parts[2]);
                    rows.Add((sym, ts, bid, ask));
                }
            }
            bookRef = new TickBook(rows);
            execution = new SimulatedExecutionAdapter(bookRef);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to build tick book: {ex.Message}");
    }
}

// Extract risk config + equity from raw JSON (tolerant: defaults if missing)
decimal equity = 100_000m; // fallback
RiskConfig riskConfig = new();
try
{
    if (raw.RootElement.TryGetProperty("equity", out var eqEl) && eqEl.ValueKind == JsonValueKind.Number) equity = eqEl.GetDecimal();
    if (raw.RootElement.TryGetProperty("risk", out var riskEl) && riskEl.ValueKind == JsonValueKind.Object)
    {
        decimal TryNum(string name, decimal fallback)
        {
            // accept both snake_case and camelCase variants
            if (riskEl.TryGetProperty(name, out var v) && v.ValueKind==JsonValueKind.Number) return v.GetDecimal();
            // map camelCase <-> snake_case
            string alt = name.Contains('_')
                ? string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select((s,i)=> i==0 ? s : char.ToUpperInvariant(s[0])+s.Substring(1)))
                : string.Concat(name.Select(c => char.IsUpper(c) ? '_' + char.ToLowerInvariant(c) : c)).TrimStart('_');
            if (riskEl.TryGetProperty(alt, out var v2) && v2.ValueKind==JsonValueKind.Number) return v2.GetDecimal();
            return fallback;
        }
        bool TryBool(string name, bool fallback)
        {
            if (riskEl.TryGetProperty(name, out var v) && (v.ValueKind==JsonValueKind.True || v.ValueKind==JsonValueKind.False)) return v.GetBoolean();
            string alt = name.Contains('_')
                ? string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select((s,i)=> i==0 ? s : char.ToUpperInvariant(s[0])+s.Substring(1)))
                : string.Concat(name.Select(c => char.IsUpper(c) ? '_' + char.ToLowerInvariant(c) : c)).TrimStart('_');
            if (riskEl.TryGetProperty(alt, out var v2) && (v2.ValueKind==JsonValueKind.True || v2.ValueKind==JsonValueKind.False)) return v2.GetBoolean();
            return fallback;
        }
        string TryStr(string name, string fallback)
        {
            if (riskEl.TryGetProperty(name, out var v) && v.ValueKind==JsonValueKind.String) return v.GetString() ?? fallback;
            string alt = name.Contains('_')
                ? string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select((s,i)=> i==0 ? s : char.ToUpperInvariant(s[0])+s.Substring(1)))
                : string.Concat(name.Select(c => char.IsUpper(c) ? '_' + char.ToLowerInvariant(c) : c)).TrimStart('_');
            if (riskEl.TryGetProperty(alt, out var v2) && v2.ValueKind==JsonValueKind.String) return v2.GetString() ?? fallback;
            return fallback;
        }
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
var enforcer = new RiskEnforcer(riskFormulas, basketAgg, cfg.SchemaVersion ?? TiYf.Engine.Core.Infrastructure.Schema.Version, cfgHash);

// Strategy size units (defaults) from config strategy.params
long sizeUnitsFx = 1000; long sizeUnitsXau = 1;
try
{
    if (raw.RootElement.TryGetProperty("strategy", out var stratNode) && stratNode.TryGetProperty("params", out var pNode))
    {
        if (pNode.TryGetProperty("sizeUnitsFx", out var fxNode) && fxNode.ValueKind==JsonValueKind.Number) sizeUnitsFx = (long)fxNode.GetInt32();
        if (pNode.TryGetProperty("sizeUnitsXau", out var xNode) && xNode.ValueKind==JsonValueKind.Number) sizeUnitsXau = (long) xNode.GetInt32();
    }
}
catch { }

var loop = new EngineLoop(clock, builders, barKeyTracker!, journal, tickSource, cfg.BarOutputEventType ?? "BAR_V1", () =>
{
    // Persist after each emitted bar (simple, can batch later)
    BarKeyTrackerPersistence.Save(snapshotPath, (InMemoryBarKeyTracker)barKeyTracker, cfg.SchemaVersion ?? TiYf.Engine.Core.Infrastructure.Schema.Version, EngineInstanceId);
}, riskFormulas, basketAgg, cfgHash, cfg.SchemaVersion ?? TiYf.Engine.Core.Infrastructure.Schema.Version, enforcer, riskConfig, equity, 
    deterministicStrategy: isM0 ? new DeterministicScriptStrategy(clock, catalog.All(), sequence.First()) : null,
    execution: execution,
    positions: positions,
    tradesWriter: tradesWriter,
    dataVersion: dataVersion,
    sizeUnitsFx: sizeUnitsFx,
    sizeUnitsXau: sizeUnitsXau,
    riskProbeEnabled: !(raw.RootElement.TryGetProperty("featureFlags", out var ff) && ff.ValueKind==JsonValueKind.Object && ff.TryGetProperty("riskProbe", out var rp) && rp.ValueKind==JsonValueKind.String && rp.GetString()=="disabled"))
{
    // future injection points if needed
};
await loop.RunAsync();

Console.WriteLine("Engine run complete.");
if (tradesWriter is not null) await tradesWriter.DisposeAsync();

// If --out provided, copy the produced journal events file to that path for deterministic tooling
if (!string.IsNullOrWhiteSpace(outPath))
{
    try
    {
    var sourceEvents = Path.Combine(journalRoot, runId, "events.csv");
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