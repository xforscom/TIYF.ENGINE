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
bool verbose = false; bool diagnose = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--config" && i + 1 < args.Length)
        configPath = args[i + 1];
    if (args[i] == "--out" && i + 1 < args.Length)
        outPath = args[i + 1];
    if (args[i] == "--run-id" && i + 1 < args.Length)
        runIdOverride = args[i + 1];
    if (args[i] == "--verbose" || args[i] == "-v") verbose = true;
    if (args[i] == "--diagnose") diagnose = true;
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
    if (!isM0 && raw.RootElement.TryGetProperty("data", out var dProbe) && dProbe.TryGetProperty("ticks", out var tProbe) && tProbe.ValueKind == JsonValueKind.Object)
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
        if (raw.RootElement.TryGetProperty("output", out var outNode) && outNode.TryGetProperty("journalDir", out var jd) && jd.ValueKind == JsonValueKind.String)
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
        // Optional diagnostics: track per-file stats for guardrail
        var diag = new List<(string Sym, string Path, bool Exists, int DataRows)>();
        foreach (var entry in tickObj.EnumerateObject())
        {
            var path = entry.Value.GetString();
            bool exists = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            int dataRows = 0;
            if (exists)
            {
                foreach (var line in SafeReadLines(path).Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    if (parts.Length < 4) continue;
                    dataRows++;
                    var ts = DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
                    allTs.Add(ts);
                }
            }
            diag.Add((entry.Name, path ?? string.Empty, exists, dataRows));
        }
        sequence = allTs.OrderBy(t => t).ToList();
        if (sequence.Count == 0)
        {
            Console.Error.WriteLine("No timestamps found when building M0 tick sequence. Diagnostics:");
            foreach (var d in diag)
                Console.Error.WriteLine($"  symbol={d.Sym} path={d.Path} exists={d.Exists.ToString().ToLowerInvariant()} data_rows={d.DataRows}");
            // Exit gracefully so CI shows actionable info
            return;
        }
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
// Guardrail: if M0 and no timestamps aggregated, emit diagnostics and exit gracefully
if (isM0 && (sequence == null || sequence.Count == 0))
{
    try
    {
        Console.Error.WriteLine("Data QA guardrail: no timestamps aggregated from M0 tick files. Diagnostics:");
        if (raw.RootElement.TryGetProperty("data", out var dNode) && dNode.ValueKind == JsonValueKind.Object &&
            dNode.TryGetProperty("ticks", out var tNode) && tNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in tNode.EnumerateObject())
            {
                var sym = entry.Name;
                var p = entry.Value.GetString();
                var exists = (!string.IsNullOrWhiteSpace(p)) && File.Exists(p);
                int dataRows = 0;
                if (exists)
                {
                    try { dataRows = SafeReadLines(p).Skip(1).Count(); } catch { /* ignore */ }
                }
                Console.Error.WriteLine($" - {sym}: path='{p}', exists={exists}, data_rows={dataRows}");
            }
        }
        else
        {
            Console.Error.WriteLine(" - config.data.ticks is missing or invalid");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Guardrail diagnostics failed: {ex.Message}");
    }
    Console.Error.WriteLine("Aborting run due to empty aggregated timestamp sequence.");
    Environment.Exit(2);
}
var clock = new DeterministicSequenceClock(sequence);

ITickSource tickSource = isM0 ? new MultiInstrumentTickSource(raw) : new CsvTickSource(cfg.InputTicksFile, instrument.Id);

// Determine instrument set
List<InstrumentId> instrumentIds = isM0
    ? catalog.All().Select(i => i.Id).ToList()
    : (cfg.Instruments is { Length: > 0 } ? cfg.Instruments.Select(s => new InstrumentId(s)).Distinct().ToList() : new List<InstrumentId> { instrument.Id });

// Determine intervals
BarInterval MapInterval(string code) => code.ToUpperInvariant() switch
{
    "M1" => BarInterval.OneMinute,
    "H1" => BarInterval.OneHour,
    "D1" => BarInterval.OneDay,
    _ => throw new ArgumentException($"Unsupported interval {code}")
};
var intervals = (cfg.Intervals is { Length: > 0 } ? cfg.Intervals : new[] { "M1" })
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
        if (raw.RootElement.GetProperty("data").TryGetProperty("instrumentsFile", out var instEl) && instEl.ValueKind == JsonValueKind.String)
            paths.Add(instEl.GetString()!);
        foreach (var p in rootProp.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) paths.Add(p.Value.GetString()!);
        // Also include config file itself if we can resolve path (cfg.ConfigPath if available else skip)
        // Intentionally exclude the config file itself so data_version reflects ONLY raw market data fixtures (stable across config param tweaks)
        // Normalize to repo-relative existing paths only
        var existing = paths.Where(File.Exists).ToArray();
        if (existing.Length > 0)
            dataVersion = TiYf.Engine.Core.DataVersion.Compute(existing);
    }
}
catch { /* Non-fatal; omit data_version if any parsing fails */ }

// Determine run id (support explicit --run-id for promotion parity tests)
string runId;
if (isM0)
{
    // Priority: explicit --run-id > config RunId > synthesized test id
    if (!string.IsNullOrWhiteSpace(runIdOverride))
    {
        runId = runIdOverride.StartsWith("M0-RUN", StringComparison.Ordinal) ? runIdOverride : $"M0-RUN-{runIdOverride}";
    }
    else if (!string.IsNullOrWhiteSpace(cfg.RunId))
    {
        runId = cfg.RunId!.StartsWith("M0-RUN", StringComparison.Ordinal) ? cfg.RunId! : $"M0-RUN-{cfg.RunId}";
    }
    else
    {
        // Historical deterministic run id for M0 tests; add suffix when explicit risk flag present to avoid cross-test collisions
        try
        {
            if (raw.RootElement.TryGetProperty("featureFlags", out var ffR) && ffR.ValueKind == JsonValueKind.Object && ffR.TryGetProperty("risk", out var rMode) && rMode.ValueKind == JsonValueKind.String)
            {
                var r = (rMode.GetString() ?? "off").ToLowerInvariant();
                runId = $"M0-RUN-{r}";
            }
            else
            {
                runId = "M0-RUN";
            }
        }
        catch { runId = "M0-RUN"; }
    }
    // Data QA stability: never rewrite ids containing 'dataqa'
    if (runId.IndexOf("dataqa", StringComparison.OrdinalIgnoreCase) >= 0)
    {
        // unchanged; placeholder for future logic if needed
    }
}
else
{
    runId = runIdOverride ?? cfg.RunId ?? "RUN";
}
var journalRoot = isM0 && !string.IsNullOrWhiteSpace(m0JournalDir) ? m0JournalDir : (cfg.JournalRoot ?? (isM0 ? "journals/M0" : "journals"));
// For M0 determinism & parallel test safety, serialize access to the single run folder via a named mutex
System.Threading.Mutex? m0Mutex = null; bool m0Locked = false;
try
{
    if (isM0)
    {
        m0Mutex = new System.Threading.Mutex(false, "Global\\TIYF.M0.RUNLOCK");
        // Wait up to 2 minutes to avoid deadlocks in CI; if cannot acquire, proceed best-effort
        try { m0Locked = m0Mutex.WaitOne(TimeSpan.FromMinutes(2)); } catch { m0Locked = false; }
        var runDir = Path.Combine(journalRoot, runId);
        if (Directory.Exists(runDir))
        {
            try { Directory.Delete(runDir, true); } catch { /* best effort */ }
        }
    }
}
catch { }
// Prepare Data QA (shadow/active) if configured in JSON (dataQA node + featureFlags.dataQa)
List<JournalEvent>? qaEvents = null;
bool qaAbort = false;
try
{
    // Determine mode via featureFlags.dataQa (default shadow). Values: shadow|active|off
    string dataQaMode = "shadow";
    try
    {
        if (raw.RootElement.TryGetProperty("featureFlags", out var ffNode) && ffNode.ValueKind == JsonValueKind.Object && ffNode.TryGetProperty("dataQa", out var dqModeNode) && dqModeNode.ValueKind == JsonValueKind.String)
        {
            var m = dqModeNode.GetString();
            if (!string.IsNullOrWhiteSpace(m)) dataQaMode = m!; // trust input
        }
    }
    catch { /* default shadow */ }
    if (raw.RootElement.TryGetProperty("dataQA", out var qaNode) && qaNode.ValueKind == JsonValueKind.Object && dataQaMode != "off")
    {
        bool enabled = qaNode.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
        if (enabled)
        {
            int maxMissing = qaNode.TryGetProperty("maxMissingBarsPerInstrument", out var mm) && mm.ValueKind == JsonValueKind.Number ? mm.GetInt32() : 0;
            bool allowDup = qaNode.TryGetProperty("allowDuplicates", out var ad) && ad.ValueKind == JsonValueKind.True;
            decimal spikeZ = qaNode.TryGetProperty("spikeZ", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetDecimal() : 8m;
            int ffill = 0;
            if (qaNode.TryGetProperty("repair", out var rep) && rep.ValueKind == JsonValueKind.Object && rep.TryGetProperty("forwardFillBars", out var ffb) && ffb.ValueKind == JsonValueKind.Number)
                ffill = ffb.GetInt32();
            bool dropSpikes = qaNode.TryGetProperty("repair", out var rep2) && rep2.ValueKind == JsonValueKind.Object && rep2.TryGetProperty("dropSpikes", out var ds) && ds.ValueKind == JsonValueKind.True;
            var dqCfg = new TiYf.Engine.Core.DataQaConfig(true, maxMissing, allowDup, spikeZ, ffill, dropSpikes);
            // Collect ticks per symbol (using bookRef later or raw fixture paths for M0)
            var ticksBySymbol = new Dictionary<string, List<(DateTime, decimal)>>(StringComparer.Ordinal);
            if (isM0 && raw.RootElement.TryGetProperty("data", out var dnode) && dnode.TryGetProperty("ticks", out var tnode) && tnode.ValueKind == JsonValueKind.Object)
            {
                foreach (var tk in tnode.EnumerateObject())
                {
                    var path = tk.Value.GetString(); if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                    var list = new List<(DateTime, decimal)>();
                    foreach (var line in SafeReadLines(path).Skip(1))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var parts = line.Split(','); if (parts.Length < 3) continue;
                        var ts = DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
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
            var toleranceObj = new
            {
                maxMissingBarsPerInstrument = dqCfg.MaxMissingBarsPerInstrument,
                allowDuplicates = dqCfg.AllowDuplicates,
                spikeZ = dqCfg.SpikeZ,
                forwardFillBars = dqCfg.ForwardFillBars,
                dropSpikes = dqCfg.DropSpikes
            };
            string toleranceJson = System.Text.Json.JsonSerializer.Serialize(toleranceObj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            string toleranceProfileHash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(toleranceJson);
                toleranceProfileHash = string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("X2")));
            }
            qaEvents = new List<JournalEvent>();
            if (ticksBySymbol.Count > 0)
            {
                var earliest = ticksBySymbol.SelectMany(k => k.Value).Select(v => v.Item1).OrderBy(t => t).FirstOrDefault();
                DateTime tsBase = earliest == default ? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) : earliest;
                // BEGIN
                var beginPayload = JsonSerializer.SerializeToElement(new
                {
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
                        var issuePayload = JsonSerializer.SerializeToElement(new
                        {
                            symbol = issue.Symbol,
                            kind = issue.Kind,
                            ts = issue.Ts,
                            details = issue.Details
                        });
                        qaEvents.Add(new JournalEvent(0, issue.Ts, "DATA_QA_ISSUE_V1", issuePayload));
                    }
                }
                bool abortedFlagLocal = (!dqResult.Passed && dataQaMode == "active");
                Console.WriteLine($"QA_DECISION passed={dqResult.Passed.ToString().ToLowerInvariant()} aborted={abortedFlagLocal.ToString().ToLowerInvariant()} mode={dataQaMode}");
                var summaryPayload = JsonSerializer.SerializeToElement(new
                {
                    symbols_checked = dqResult.SymbolsChecked,
                    issues = dqResult.Issues,
                    repaired = dqResult.Repaired,
                    passed = dqResult.Passed,
                    tolerated_count = toleratedCount,
                    aborted = abortedFlagLocal,
                    tolerance_profile_hash = toleranceProfileHash
                });
                qaEvents.Add(new JournalEvent(0, tsBase, "DATA_QA_SUMMARY_V1", summaryPayload));
                if (!dqResult.Passed && dataQaMode == "active")
                {
                    // Derive reason deterministically
                    string reason = "unknown";
                    if (dqResult.IssuesList.Any(i => i.Kind == "missing_bar")) reason = "missing_bars_exceeded";
                    else if (dqResult.IssuesList.Any(i => i.Kind == "duplicate")) reason = "duplicates_not_allowed";
                    else if (dqResult.IssuesList.Any(i => i.Kind == "spike")) reason = "spike_threshold_exceeded";
                    var abortPayload = JsonSerializer.SerializeToElement(new
                    {
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
    else if (cfg.MaxMissingBarsPerInstrument > 0)
    {
        // Drop up to K missing_bar issues per symbol, deterministically by timestamp
        var bySym = filtered
            .Where(i => i.Kind == "missing_bar")
            .GroupBy(i => i.Symbol);

        foreach (var g in bySym)
        {
            var items = g.OrderBy(i => i.Ts).ToList();
            int drop = Math.Min(cfg.MaxMissingBarsPerInstrument, items.Count);
            for (int k = 0; k < drop; k++)
            {
                filtered.Remove(items[k]);
            }
        }
    }
    // else K == 0 -> no tolerance; keep all missing_bar issues

    // Spikes: if spikeZ very large OR dropSpikes==false treat spike issues as tolerated (removed)
    if (cfg.SpikeZ >= 50m || !cfg.DropSpikes)
        filtered.RemoveAll(i => i.Kind == "spike");

    int repaired = raw.Repaired; // we don't mutate repaired here (only analyzer modifies)
    bool passed = filtered.Count == 0; // after tolerance filtering
    return new TiYf.Engine.Core.DataQaResult(passed, raw.SymbolsChecked, filtered.Count, repaired, filtered);
}

// Journal writer with optional data_version (open before emitting QA events)
await using var journal = new FileJournalWriter(journalRoot, runId, cfg.SchemaVersion ?? TiYf.Engine.Core.Infrastructure.Schema.Version, cfgHash, dataVersion);
try
{
    Console.WriteLine($"RUN_ID_RESOLVED={runId}");
    Console.WriteLine($"JOURNAL_DIR_EVENTS={Path.Combine(journalRoot, runId, "events.csv")}");
    Console.WriteLine($"JOURNAL_DIR_TRADES={Path.Combine(journalRoot, runId, "trades.csv")}");
}
catch { }
if (qaEvents is not null && qaEvents.Count > 0)
{
    qaEvents = qaEvents
        .OrderBy(e => e.UtcTimestamp)
        .ThenBy(e => e.EventType, StringComparer.Ordinal)
        .ThenBy(e => e.Sequence)
        .ToList();
    await journal.AppendRangeAsync(qaEvents);
}

Console.WriteLine($"ABORTED={qaAbort.ToString().ToLowerInvariant()}");

if (qaAbort)
{
    Console.WriteLine("DATA_QA gate failed – aborting prior to bar/trade processing.");
    return;
}
// Load snapshot now that paths are final
var snapshotPath = Path.Combine(journalRoot, runId, "bar-keys.snapshot.json");
barKeyTracker = BarKeyTrackerPersistence.Load(snapshotPath);
TradesJournalWriter? tradesWriter = null; PositionTracker? positions = null; IExecutionAdapter? execution = null; TickBook? bookRef = null;
if (raw.RootElement.TryGetProperty("name", out var nmEl) && nmEl.ValueKind == JsonValueKind.String && (nmEl.GetString() == "backtest-m0" || (nmEl.GetString()?.StartsWith("backtest-m0", StringComparison.Ordinal) ?? false)))
{
    positions = new PositionTracker();
    tradesWriter = new TradesJournalWriter(journalRoot, runId, cfg.SchemaVersion ?? TiYf.Engine.Core.Infrastructure.Schema.Version, cfgHash, dataVersion);
    // Build multi-instrument tick book from fixture files if present
    try
    {
        if (raw.RootElement.TryGetProperty("data", out var dataNode) && dataNode.TryGetProperty("ticks", out var ticksNode) && ticksNode.ValueKind == JsonValueKind.Object)
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
                    var ts = DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
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
    // Accept both legacy "risk" and newer "riskConfig" blocks, later entries override earlier ones
    var riskBlocks = new List<JsonElement>();
    if (raw.RootElement.TryGetProperty("risk", out var legacyRisk) && legacyRisk.ValueKind == JsonValueKind.Object) riskBlocks.Add(legacyRisk);
    if (raw.RootElement.TryGetProperty("riskConfig", out var rcBlock) && rcBlock.ValueKind == JsonValueKind.Object) riskBlocks.Add(rcBlock);
    if (riskBlocks.Count > 0)
    {
        decimal TryNum(string name, decimal? fallbackNullable)
        {
            foreach (var blk in riskBlocks)
            {
                if (blk.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
                // camel/snake translation
                string alt = name.Contains('_')
                    ? string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select((s, i) => i == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1)))
                    : string.Concat(name.Select(c => char.IsUpper(c) ? '_' + char.ToLowerInvariant(c) : c)).TrimStart('_');
                if (blk.TryGetProperty(alt, out var v2) && v2.ValueKind == JsonValueKind.Number) return v2.GetDecimal();
                // case-insensitive fallback scan (captures variants like maxRunDrawdownCCY with different casing)
                if (blk.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in blk.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Number && prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return prop.Value.GetDecimal();
                        if (prop.Value.ValueKind == JsonValueKind.Number && prop.Name.Equals(alt, StringComparison.OrdinalIgnoreCase)) return prop.Value.GetDecimal();
                    }
                }
            }
            return fallbackNullable ?? 0m;
        }
        bool TryBool(string name, bool fallback)
        {
            foreach (var blk in riskBlocks)
            {
                if (blk.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)) return v.GetBoolean();
                string alt = name.Contains('_')
                    ? string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select((s, i) => i == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1)))
                    : string.Concat(name.Select(c => char.IsUpper(c) ? '_' + char.ToLowerInvariant(c) : c)).TrimStart('_');
                if (blk.TryGetProperty(alt, out var v2) && (v2.ValueKind == JsonValueKind.True || v2.ValueKind == JsonValueKind.False)) return v2.GetBoolean();
            }
            return fallback;
        }
        string TryStr(string name, string fallback)
        {
            foreach (var blk in riskBlocks)
            {
                if (blk.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString() ?? fallback;
                string alt = name.Contains('_')
                    ? string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select((s, i) => i == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1)))
                    : string.Concat(name.Select(c => char.IsUpper(c) ? '_' + char.ToLowerInvariant(c) : c)).TrimStart('_');
                if (blk.TryGetProperty(alt, out var v2) && v2.ValueKind == JsonValueKind.String) return v2.GetString() ?? fallback;
            }
            return fallback;
        }
        var buckets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var blk in riskBlocks)
        {
            if (blk.TryGetProperty("instrument_buckets", out var bEl) && bEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in bEl.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) buckets[p.Name] = p.Value.GetString() ?? string.Empty;
            }
        }
        var exposureDict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var blk in riskBlocks)
        {
            if (blk.TryGetProperty("maxNetExposureBySymbol", out var expNode) || blk.TryGetProperty("max_net_exposure_by_symbol", out expNode))
            {
                if (expNode.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p2 in expNode.EnumerateObject()) if (p2.Value.ValueKind == JsonValueKind.Number) exposureDict[p2.Name] = p2.Value.GetDecimal();
                }
            }
        }
        var maxRunDD = TryNum("max_run_drawdown_ccy", null);
        if (maxRunDD == 0m) maxRunDD = 0m; // interpret 0 as null below
        // Parse force_drawdown_after_evals (object map symbol->int)
        Dictionary<string, int>? forceDdMap = null;
        foreach (var blk in riskBlocks)
        {
            if (blk.TryGetProperty("forceDrawdownAfterEvals", out var fd) || blk.TryGetProperty("force_drawdown_after_evals", out fd))
            {
                if (fd.ValueKind == JsonValueKind.Object)
                {
                    forceDdMap ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in fd.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) forceDdMap[p.Name] = p.Value.GetInt32();
                }
            }
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
            LotStep = TryNum("lot_step", 0.01m),
            EmitEvaluations = TryBool("emit_evaluations", true),
            BlockOnBreach = TryBool("block_on_breach", true),
            MaxRunDrawdownCCY = maxRunDD == 0m ? null : maxRunDD,
            MaxNetExposureBySymbol = exposureDict.Count == 0 ? null : exposureDict,
            ForceDrawdownAfterEvals = forceDdMap
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
TiYf.Engine.Core.RiskMode parsedRiskModeEnum = TiYf.Engine.Core.RiskMode.Off;
try
{
    if (raw.RootElement.TryGetProperty("featureFlags", out var ffNode2))
    {
        var node2 = System.Text.Json.Nodes.JsonNode.Parse(ffNode2.GetRawText());
        parsedRiskModeEnum = TiYf.Engine.Core.RiskParsing.ParseRiskMode(node2);
    }
}
catch { }
var parsedRiskMode = parsedRiskModeEnum switch { TiYf.Engine.Core.RiskMode.Active => "active", TiYf.Engine.Core.RiskMode.Shadow => "shadow", _ => "off" };
Console.WriteLine($"RUN_ID={runId}");
// Penalty feature flag parse (shadow scaffold) + debug echo when verbose/diagnose
string penaltyMode = "off"; bool forcePenalty = false;
bool ciPenaltyScaffold = false; // new root-level opt-in for CI-only forced penalty emission
try
{
    if (raw.RootElement.TryGetProperty("featureFlags", out var ffPen) && ffPen.ValueKind == JsonValueKind.Object && ffPen.TryGetProperty("penalty", out var penNode) && penNode.ValueKind == JsonValueKind.String)
    {
        penaltyMode = (penNode.GetString() ?? "off").ToLowerInvariant();
    }
    if (raw.RootElement.TryGetProperty("penaltyConfig", out var pCfg) && pCfg.ValueKind == JsonValueKind.Object && pCfg.TryGetProperty("forcePenalty", out var fp) && (fp.ValueKind == JsonValueKind.True || fp.ValueKind == JsonValueKind.False))
    {
        forcePenalty = fp.ValueKind == JsonValueKind.True;
    }
    // Explicit CI scaffold opt-in (default false). Only when true do we emit the early penalty scaffold.
    if (raw.RootElement.TryGetProperty("ciPenaltyScaffold", out var ciPen) && (ciPen.ValueKind == JsonValueKind.True || ciPen.ValueKind == JsonValueKind.False))
    {
        ciPenaltyScaffold = ciPen.ValueKind == JsonValueKind.True;
    }
}
catch { }
if (verbose || diagnose)
{
    Console.WriteLine($"PENALTY_MODE_RESOLVED={penaltyMode} force={forcePenalty.ToString().ToLowerInvariant()} ci_scaffold={ciPenaltyScaffold.ToString().ToLowerInvariant()}");
}

// Strategy size units (defaults) from config strategy.params
long sizeUnitsFx = 1000; long sizeUnitsXau = 1;
try
{
    if (raw.RootElement.TryGetProperty("strategy", out var stratNode) && stratNode.TryGetProperty("params", out var pNode))
    {
        if (pNode.TryGetProperty("sizeUnitsFx", out var fxNode) && fxNode.ValueKind == JsonValueKind.Number) sizeUnitsFx = (long)fxNode.GetInt32();
        if (pNode.TryGetProperty("sizeUnitsXau", out var xNode) && xNode.ValueKind == JsonValueKind.Number) sizeUnitsXau = (long)xNode.GetInt32();
    }
}
catch { }

SentimentGuardConfig? BuildSentimentConfig(JsonDocument rawDoc)
{
    try
    {
        string mode = "shadow"; // default shadow per spec
        if (rawDoc.RootElement.TryGetProperty("featureFlags", out var ffNode) && ffNode.ValueKind == JsonValueKind.Object)
        {
            if (ffNode.TryGetProperty("sentiment", out var sentNode) && sentNode.ValueKind == JsonValueKind.String)
            {
                mode = sentNode.GetString() ?? "shadow"; // off|shadow|active
            }
        }
        if (mode.Equals("off", StringComparison.OrdinalIgnoreCase) || mode.Equals("disabled", StringComparison.OrdinalIgnoreCase) || mode.Equals("none", StringComparison.OrdinalIgnoreCase)) return null; // treat synonyms as off
        // Optional nested sentiment config: sentimentConfig: { window: 20, volGuardSigma: 0.05 }
        int window = 20; decimal sigma = 0.10m;
        if (rawDoc.RootElement.TryGetProperty("sentimentConfig", out var sc) && sc.ValueKind == JsonValueKind.Object)
        {
            if (sc.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.Number) window = w.GetInt32();
            if (sc.TryGetProperty("volGuardSigma", out var s) && s.ValueKind == JsonValueKind.Number) sigma = s.GetDecimal();
        }
        return new SentimentGuardConfig(true, window, sigma, mode);
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
    riskProbeEnabled: !(raw.RootElement.TryGetProperty("featureFlags", out var ff) && ff.ValueKind == JsonValueKind.Object && ff.TryGetProperty("riskProbe", out var rp) && rp.ValueKind == JsonValueKind.String && rp.GetString() == "disabled"),
    sentimentConfig: BuildSentimentConfig(raw),
    penaltyConfig: penaltyMode,
    forcePenalty: forcePenalty,
    ciPenaltyScaffold: ciPenaltyScaffold
        , riskMode: parsedRiskMode
);
await loop.RunAsync();

Console.WriteLine("Engine run complete.");
if (tradesWriter is not null) await tradesWriter.DisposeAsync();
await journal.DisposeAsync();

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

// Release M0 mutex if held
try { if (m0Locked) m0Mutex?.ReleaseMutex(); m0Mutex?.Dispose(); } catch { }

// ------------------------------------------------------------
// Parity Artifact Generation (artifact-only, no journal events)
//   artifacts/parity/<run-id>/hashes.txt
//   events_sha=<SHA256 normalized events.csv>
//   trades_sha=<SHA256 normalized trades.csv>
//   applied_count=<INFO_SENTIMENT_APPLIED_V1 occurrences>
//   penalty_count=<PENALTY_APPLIED_V1 occurrences>
// Normalization rules:
//   * Strip meta + header lines
//   * Convert CRLF -> LF
//   * Drop config_hash column (events: meta line contains config_hash already; trades: remove the config_hash column entirely before hashing)
//   * Preserve field order from source after column removal
// ------------------------------------------------------------
try
{
    var runDir = Path.Combine(journalRoot, runId);
    var eventsPath = Path.Combine(runDir, "events.csv");
    var tradesPath = Path.Combine(runDir, "trades.csv");
    if (File.Exists(eventsPath))
    {
        Directory.CreateDirectory(Path.Combine(runDir, "..", "..")); // ensure journals root present (defensive)
        var parityDirName = runId.StartsWith("M0-RUN-", StringComparison.Ordinal) ? runId.Substring("M0-RUN-".Length) : runId;
        var parityDir = Path.Combine("artifacts", "parity", parityDirName); // retain trimmed naming to align with tests
        Directory.CreateDirectory(parityDir);

        string NormalizeEvents(string path)
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length <= 2) return string.Empty; // only meta+header or empty
            // Skip meta + header
            var data = lines.Skip(2);
            var sb = new System.Text.StringBuilder();
            bool first = true;
            foreach (var raw in data)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var line = raw.Replace("\r\n", "\n").Replace("\r", "");
                // events.csv does not include config_hash as a separate column (it's in meta line) so nothing to drop
                if (!first) sb.Append('\n'); first = false; sb.Append(line);
            }
            return sb.ToString();
        }

        string NormalizeTrades(string path)
        {
            if (!File.Exists(path)) return string.Empty;
            var lines = File.ReadAllLines(path);
            if (lines.Length <= 1) return string.Empty; // header only
            var header = lines[0].Split(',');
            var dropIdx = Array.FindIndex(header, h => h.Equals("config_hash", StringComparison.OrdinalIgnoreCase));
            var keepIdx = new List<int>();
            for (int i = 0; i < header.Length; i++) if (i != dropIdx) keepIdx.Add(i);
            var sb = new System.Text.StringBuilder(); bool firstLine = true;
            foreach (var raw in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var parts = raw.Split(',');
                var proj = keepIdx.Select(i => i < parts.Length ? parts[i] : string.Empty);
                var line = string.Join(',', proj).Replace("\r\n", "\n").Replace("\r", "");
                if (!firstLine) sb.Append('\n'); firstLine = false; sb.Append(line);
            }
            return sb.ToString();
        }

        string Sha256(string content)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            var hash = sha.ComputeHash(bytes);
            return string.Concat(hash.Select(b => b.ToString("X2")));
        }

        var normEvents = NormalizeEvents(eventsPath);
        var normTrades = NormalizeTrades(tradesPath);
        var eventsSha = Sha256(normEvents);
        var tradesSha = Sha256(normTrades);
        int appliedCount = 0, penaltyCount = 0;
        try
        {
            foreach (var line in File.ReadLines(eventsPath))
            {
                if (line.Contains("INFO_SENTIMENT_APPLIED_V1", StringComparison.Ordinal)) appliedCount++;
                else if (line.Contains("PENALTY_APPLIED_V1", StringComparison.Ordinal)) penaltyCount++;
            }
        }
        catch { /* ignore count errors */ }

        var hashFile = Path.Combine(parityDir, "hashes.txt");
        var linesOut = new[]
        {
            $"events_sha={eventsSha}",
            $"trades_sha={tradesSha}",
            $"applied_count={appliedCount}",
            $"penalty_count={penaltyCount}"
        };
        File.WriteAllLines(hashFile, linesOut);
        Console.WriteLine($"Parity artifacts written: {hashFile}");
    }
}
catch (Exception pax)
{
    Console.Error.WriteLine($"Parity artifact generation failed: {pax.Message}");
}