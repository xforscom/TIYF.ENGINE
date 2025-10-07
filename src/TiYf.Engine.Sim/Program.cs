using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Sidecar;
using TiYf.Engine.Sim;

const string EngineInstanceId = "engine-local-1"; // could be GUID in future

// Basic CLI harness: dotnet run --project src/TiYf.Engine.Sim -- --config sample-config.json

// Defensive wrapper to avoid unhandled exceptions if a config supplies a null/empty path unexpectedly.
IEnumerable<string> SafeReadLines(string? p)
{
    if (string.IsNullOrWhiteSpace(p)) yield break;
    IEnumerable<string> lines;
    try { lines = File.ReadLines(p); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Warning: failed to read lines from '{p}': {ex.Message}");
        yield break;
    }
    foreach (var l in lines) yield return l;
}

string? configPath = null;
string? outPath = null;
string? runIdOverride = null; // provided via --run-id for deterministic multi-run parity tests
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--config" && i + 1 < args.Length)
        configPath = args[i + 1];
    if (args[i] == "--out" && i + 1 < args.Length)
        outPath = args[i + 1];
    if (args[i] == "--run-id" && i + 1 < args.Length)
        runIdOverride = args[i + 1];
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
bool isM0 = false;
try
{
    if (raw.RootElement.TryGetProperty("name", out var nameNode))
    {
        var nm = nameNode.GetString() ?? string.Empty;
        // Treat any name that starts with backtest-m0 (candidate / degrade variants) as M0 fixture family
        if (nm.StartsWith("backtest-m0", StringComparison.Ordinal)) isM0 = true;
    }
    // Fallback heuristic: presence of data.ticks object implies M0-style multi-instrument fixture
    if (!isM0 && raw.RootElement.TryGetProperty("data", out var dProbe) && dProbe.TryGetProperty("ticks", out var tProbe) && tProbe.ValueKind==JsonValueKind.Object)
        isM0 = true;
}
catch { /* default false */ }
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
            foreach (var line in SafeReadLines(path).Skip(1))
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
    if (!string.IsNullOrWhiteSpace(cfg.InputTicksFile) && File.Exists(cfg.InputTicksFile))
    {
        foreach (var line in SafeReadLines(cfg.InputTicksFile).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            sequence.Add(DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal));
        }
    }
    else
    {
        Console.Error.WriteLine("Warning: Legacy InputTicksFile not provided or missing – proceeding with empty sequence (no bars). Provide --config with valid ticks or use M0 fixture.");
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

// Determine run id (support explicit --run-id for promotion parity tests)
string runId;
if (isM0)
{
    if (!string.IsNullOrWhiteSpace(runIdOverride))
    {
        runId = $"M0-RUN-{runIdOverride}"; // e.g. M0-RUN-candA
    }
    else if (!string.IsNullOrWhiteSpace(cfg.RunId))
    {
        // If config already supplies an M0 style id leave it, else prefix
        runId = cfg.RunId!.StartsWith("M0-RUN", StringComparison.Ordinal) ? cfg.RunId! : $"M0-RUN-{cfg.RunId}";
    }
    else
    {
        runId = "M0-RUN"; // baseline deterministic default
    }
}
else
{
    runId = runIdOverride ?? cfg.RunId ?? "RUN";
}
var journalRoot = isM0 && !string.IsNullOrWhiteSpace(m0JournalDir) ? m0JournalDir : (cfg.JournalRoot ?? (isM0 ? "journals/M0" : "journals"));
// For M0 determinism, ensure a clean run directory each invocation (avoid stale appended events creating false alerts)
if (isM0)
{
    var runDir = Path.Combine(journalRoot, runId);
    if (Directory.Exists(runDir))
    {
        try { Directory.Delete(runDir, true); } catch { /* best effort */ }
    }
}
// Prepare Data QA (shadow/active) if configured in JSON (dataQA node + featureFlags.dataQa)
List<JournalEvent>? qaEvents = null;
bool qaAbort = false;
try
{
    // Determine mode via featureFlags.dataQa (default shadow). Values: shadow|active|off
    string dataQaMode = "shadow";
    try
    {
        if (raw.RootElement.TryGetProperty("featureFlags", out var ffNode) && ffNode.ValueKind==JsonValueKind.Object && ffNode.TryGetProperty("dataQa", out var dqModeNode) && dqModeNode.ValueKind==JsonValueKind.String)
        {
            var m = dqModeNode.GetString();
            if (!string.IsNullOrWhiteSpace(m)) dataQaMode = m!; // trust input
        }
    }
    catch { /* default shadow */ }
    if (raw.RootElement.TryGetProperty("dataQA", out var qaNode) && qaNode.ValueKind==JsonValueKind.Object && dataQaMode != "off")
    {
        bool enabled = qaNode.TryGetProperty("enabled", out var en) && en.ValueKind==JsonValueKind.True;
        if (enabled)
        {
            int maxMissing = qaNode.TryGetProperty("maxMissingBarsPerInstrument", out var mm) && mm.ValueKind==JsonValueKind.Number ? mm.GetInt32() : 0;
            bool allowDup = qaNode.TryGetProperty("allowDuplicates", out var ad) && ad.ValueKind==JsonValueKind.True;
            decimal spikeZ = qaNode.TryGetProperty("spikeZ", out var sz) && sz.ValueKind==JsonValueKind.Number ? sz.GetDecimal() : 8m;
            int ffill = 0; 
            if (qaNode.TryGetProperty("repair", out var rep) && rep.ValueKind==JsonValueKind.Object && rep.TryGetProperty("forwardFillBars", out var ffb) && ffb.ValueKind==JsonValueKind.Number)
                ffill = ffb.GetInt32();
            bool dropSpikes = qaNode.TryGetProperty("repair", out var rep2) && rep2.ValueKind==JsonValueKind.Object && rep2.TryGetProperty("dropSpikes", out var ds) && ds.ValueKind==JsonValueKind.True;
            var dqCfg = new TiYf.Engine.Core.DataQaConfig(true, maxMissing, allowDup, spikeZ, ffill, dropSpikes);
            // Collect ticks per symbol (using bookRef later or raw fixture paths for M0)
            var ticksBySymbol = new Dictionary<string,List<(DateTime,decimal)>>(StringComparer.Ordinal);
            if (isM0 && raw.RootElement.TryGetProperty("data", out var dnode) && dnode.TryGetProperty("ticks", out var tnode) && tnode.ValueKind==JsonValueKind.Object)
            {
                foreach (var tk in tnode.EnumerateObject())
                {
                    var path = tk.Value.GetString(); if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                    var list = new List<(DateTime,decimal)>();
                    foreach (var line in SafeReadLines(path).Skip(1))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var parts = line.Split(','); if (parts.Length < 3) continue;
                        var ts = DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.AssumeUniversal|System.Globalization.DateTimeStyles.AdjustToUniversal);
                        var mid = (decimal.Parse(parts[1]) + decimal.Parse(parts[2])) / 2m;
                        list.Add((ts, mid));
                    }
                    ticksBySymbol[tk.Name] = list;
                }
            }
            Console.WriteLine($"QA_CFG maxMissing={dqCfg.MaxMissingBarsPerInstrument} allowDup={dqCfg.AllowDuplicates} spikeZ={dqCfg.SpikeZ} ffill={dqCfg.ForwardFillBars} dropSpikes={dqCfg.DropSpikes}");
            // 1. Analyze (pure)
            var dqResultRaw = TiYf.Engine.Core.DataQaAnalyzer.Run(dqCfg, ticksBySymbol);
            // 2. Apply early tolerance before any journaling or abort gating
            var dqResult = ApplyTolerance(dqResultRaw, dqCfg);
            int toleratedCount = dqResultRaw.IssuesList.Count - dqResult.IssuesList.Count;
            // Build tolerance profile JSON (canonical) for hashing
            var toleranceObj = new {
                maxMissingBarsPerInstrument = dqCfg.MaxMissingBarsPerInstrument,
                allowDuplicates = dqCfg.AllowDuplicates,
                spikeZ = dqCfg.SpikeZ,
                forwardFillBars = dqCfg.ForwardFillBars,
                dropSpikes = dqCfg.DropSpikes
            };
            string toleranceJson = System.Text.Json.JsonSerializer.Serialize(toleranceObj, new JsonSerializerOptions{PropertyNamingPolicy = JsonNamingPolicy.CamelCase});
            string toleranceProfileHash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(toleranceJson);
                toleranceProfileHash = string.Concat(sha.ComputeHash(bytes).Select(b=>b.ToString("X2")));
            }
            qaEvents = new List<JournalEvent>();
            if (ticksBySymbol.Count > 0)
            {
                var earliest = ticksBySymbol.SelectMany(k=>k.Value).Select(v=>v.Item1).OrderBy(t=>t).FirstOrDefault();
                DateTime tsBase = earliest == default ? new DateTime(2000,1,1,0,0,0,DateTimeKind.Utc) : earliest;
                // BEGIN
                var beginPayload = JsonSerializer.SerializeToElement(new {
                    timeframe = "M1",
                    window_from = tsBase,
                    window_to = tsBase, // single window placeholder (future: derive)
                    data_version = dataVersion ?? string.Empty
                });
                qaEvents.Add(new JournalEvent(0, tsBase, "DATA_QA_BEGIN_V1", beginPayload));
                if (dqResult.IssuesList.Count > 0)
                {
                    foreach (var issue in dqResult.IssuesList
                        .OrderBy(i => i.Ts)
                        .ThenBy(i => i.Symbol, StringComparer.Ordinal)
                        .ThenBy(i => i.Kind, StringComparer.Ordinal)
                        .ThenBy(i => i.Details, StringComparer.Ordinal))
                    {
                        var issuePayload = JsonSerializer.SerializeToElement(new {
                            symbol = issue.Symbol,
                            kind = issue.Kind,
                            ts = issue.Ts,
                            details = issue.Details
                        });
                        qaEvents.Add(new JournalEvent(0, issue.Ts, "DATA_QA_ISSUE_V1", issuePayload));
                    }
                }
                var summaryPayload = JsonSerializer.SerializeToElement(new {
                    symbols_checked = dqResult.SymbolsChecked,
                    issues = dqResult.Issues,
                    repaired = dqResult.Repaired,
                    passed = dqResult.Passed,
                    tolerated_count = toleratedCount,
                    aborted = (!dqResult.Passed && dataQaMode=="active"),
                    tolerance_profile_hash = toleranceProfileHash
                });
                qaEvents.Add(new JournalEvent(0, tsBase, "DATA_QA_SUMMARY_V1", summaryPayload));
                if (!dqResult.Passed && dataQaMode=="active")
                {
                    // Derive reason deterministically
                    string reason = "unknown";
                    if (dqResult.IssuesList.Any(i=>i.Kind=="missing_bar")) reason = "missing_bars_exceeded";
                    else if (dqResult.IssuesList.Any(i=>i.Kind=="duplicate")) reason = "duplicates_not_allowed";
                    else if (dqResult.IssuesList.Any(i=>i.Kind=="spike")) reason = "spike_threshold_exceeded";
                    var abortPayload = JsonSerializer.SerializeToElement(new {
                        reason,
                        issues_emitted = dqResult.Issues,
                        tolerated = toleratedCount,
                        config_hash = cfgHash,
                        tolerance_profile_hash = toleranceProfileHash
                    });
                    qaEvents.Add(new JournalEvent(0, tsBase, "DATA_QA_ABORT_V1", abortPayload));
                    qaAbort = true;
                }
            }
        }
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Data QA phase error (non-fatal, continuing): {ex.Message}");
}

// Early tolerance application (deterministic) – performed outside analyzer to keep it pure.
static TiYf.Engine.Core.DataQaResult ApplyTolerance(TiYf.Engine.Core.DataQaResult raw, TiYf.Engine.Core.DataQaConfig cfg)
{
    // If disabled just return raw
    if (!cfg.Enabled) return raw;
    var filtered = new List<TiYf.Engine.Core.DataQaIssue>(raw.IssuesList);

    bool tolerantDuplicate = cfg.AllowDuplicates; // if true we drop duplicate issues
    if (tolerantDuplicate)
        filtered.RemoveAll(i => i.Kind == "duplicate");

    // Missing bars: if threshold extremely high (>=999) treat as tolerated (drop them)
    if (cfg.MaxMissingBarsPerInstrument >= 999)
    {
        var removed = filtered.RemoveAll(i => i.Kind == "missing_bar");
        if (removed > 0)
            Console.WriteLine($"QA_TOLERATE_MISSING removed={removed} threshold={cfg.MaxMissingBarsPerInstrument}");
    }
    else
    {
        // Otherwise enforce per-symbol threshold by truncating up to allowed count and retaining overflow (fail logic below will capture)
        var perSymMissing = filtered.Where(i=>i.Kind=="missing_bar").GroupBy(i=>i.Symbol).ToDictionary(g=>g.Key,g=>g.ToList());
        foreach (var kv in perSymMissing)
        {
            if (kv.Value.Count <= cfg.MaxMissingBarsPerInstrument) continue; // keep all (will fail gate) – we do not partially drop to keep determinism of failure diagnostics
        }
    }

    // Spikes: if spikeZ very large OR dropSpikes==false treat spike issues as tolerated (removed)
    if (cfg.SpikeZ >= 50m || !cfg.DropSpikes)
        filtered.RemoveAll(i => i.Kind == "spike");

    int repaired = raw.Repaired; // we don't mutate repaired here (only analyzer modifies)
    bool passed = filtered.Count == 0; // after tolerance filtering
    return new TiYf.Engine.Core.DataQaResult(passed, raw.SymbolsChecked, filtered.Count, repaired, filtered);
}

// Journal writer with optional data_version (open before emitting QA events)
await using var journal = new FileJournalWriter(journalRoot, runId, cfg.SchemaVersion ?? TiYf.Engine.Core.Infrastructure.Schema.Version, cfgHash, dataVersion);
            if (qaEvents is not null && qaEvents.Count>0)
            {
                qaEvents = qaEvents
                    .OrderBy(e => e.UtcTimestamp)
                    .ThenBy(e => e.EventType, StringComparer.Ordinal)
                    .ThenBy(e => e.Sequence)
                    .ToList();
                await journal.AppendRangeAsync(qaEvents);
    if (qaAbort)
    {
        Console.WriteLine("DATA_QA gate failed – aborting prior to bar/trade processing.");
        return;
    }
}
// Load snapshot now that paths are final
var snapshotPath = Path.Combine(journalRoot, runId, "bar-keys.snapshot.json");
barKeyTracker = BarKeyTrackerPersistence.Load(snapshotPath);
TradesJournalWriter? tradesWriter = null; PositionTracker? positions = null; IExecutionAdapter? execution = null; TickBook? bookRef = null;
if (raw.RootElement.TryGetProperty("name", out var nmEl) && nmEl.ValueKind==JsonValueKind.String && (nmEl.GetString()=="backtest-m0" || (nmEl.GetString()?.StartsWith("backtest-m0", StringComparison.Ordinal) ?? false)))
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
                foreach (var line in SafeReadLines(path).Skip(1))
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

SentimentGuardConfig? BuildSentimentConfig(JsonDocument rawDoc)
{
    try
    {
        if (rawDoc.RootElement.TryGetProperty("featureFlags", out var ffNode) && ffNode.ValueKind==JsonValueKind.Object && ffNode.TryGetProperty("sentiment", out var sentNode))
        {
            var mode = sentNode.ValueKind==JsonValueKind.String ? sentNode.GetString() ?? "disabled" : "disabled";
            if (mode.Equals("shadow", StringComparison.OrdinalIgnoreCase))
            {
                // Optional nested sentiment config: sentimentConfig: { window: 20, volGuardSigma: 0.05 }
                int window = 20; decimal sigma = 0.10m;
                if (rawDoc.RootElement.TryGetProperty("sentimentConfig", out var sc) && sc.ValueKind==JsonValueKind.Object)
                {
                    if (sc.TryGetProperty("window", out var w) && w.ValueKind==JsonValueKind.Number) window = w.GetInt32();
                    if (sc.TryGetProperty("volGuardSigma", out var s) && s.ValueKind==JsonValueKind.Number) sigma = s.GetDecimal();
                }
                return new SentimentGuardConfig(true, window, sigma, mode);
            }
        }
    }
    catch { }
    return null;
}

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
    riskProbeEnabled: !(raw.RootElement.TryGetProperty("featureFlags", out var ff) && ff.ValueKind==JsonValueKind.Object && ff.TryGetProperty("riskProbe", out var rp) && rp.ValueKind==JsonValueKind.String && rp.GetString()=="disabled"),
    sentimentConfig: BuildSentimentConfig(raw)
);
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