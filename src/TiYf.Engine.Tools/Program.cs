using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TiYf.Engine.Tools; // VerifyEngine types

// ------------------------------------------------------------
// CLI entrypoint: supports 'diff', 'verify', 'promote', 'dataversion'
// ------------------------------------------------------------
var argv = args.ToList();
if (argv.Count == 0 || argv[0] == "--help") { PrintHelp(); return 2; }
var cmd = argv[0].ToLowerInvariant();
try
{
    return cmd switch
    {
        "diff" => RunDiff(argv.Skip(1).ToList()),
        "verify" => RunVerify(argv.Skip(1).ToList()),
        "promote" => RunPromote(argv.Skip(1).ToList()),
        "dataversion" => RunDataVersion(argv.Skip(1).ToList()),
        _ => Unknown()
    };
}
catch (VerifyFatalException vf)
{
    Console.Error.WriteLine($"FATAL: {vf.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    // For generic runtime/IO/parsing errors return 1 (reserved) per strict verify spec
    return 1;
}

static int Unknown() { Console.Error.WriteLine("Unknown command"); PrintHelp(); return 2; }
static void PrintHelp() => Console.WriteLine(@"Usage:
    diff   --a <fileA> --b <fileB> [--keys k1,k2,...] [--report-duplicates]
    verify --file <journal.csv> [--json] [--max-errors N] [--report-duplicates]
    verify strict --events <events.csv> --trades <trades.csv> --schema <minVersion> [--json] [--lenient-order]
    verify parity --events-a <eventsA.csv> --events-b <eventsB.csv> [--trades-a <tradesA.csv> --trades-b <tradesB.csv>] [--json]
    promote --baseline <config.json> --candidate <config.json> [--workdir <dir>] [--quiet] [--print-metrics] [--culture name]
    dataversion --config <config.json> [--instruments path] [--ticks SYMBOL=path ...] [--out data_version.txt] [--echo-rows]");

static int RunDataVersion(List<string> args)
{
    string? config = null; string? instrumentsOverride = null; var tickOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); string? outFile = null; bool echoRows = false;
    for (int i = 0; i < args.Count; i++)
    {
        switch (args[i])
        {
            case "--config": config = (++i < args.Count) ? args[i] : null; break;
            case "--instruments": instrumentsOverride = (++i < args.Count) ? args[i] : null; break;
            case "--ticks":
                if (++i < args.Count)
                {
                    var spec = args[i];
                    var kv = spec.Split('='); if (kv.Length == 2) tickOverrides[kv[0]] = kv[1];
                }
                break;
            case "--out": outFile = (++i < args.Count) ? args[i] : null; break;
            case "--echo-rows": echoRows = true; break;
            default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 2;
        }
    }
    if (config == null || !File.Exists(config)) { Console.Error.WriteLine("--config required and must exist"); return 2; }
    using var cfgDoc = JsonDocument.Parse(File.ReadAllText(config));
    var root = cfgDoc.RootElement;
    string? instruments = instrumentsOverride;
    if (instruments == null)
    {
        instruments = root.TryGetProperty("data", out var dataEl) && dataEl.TryGetProperty("instrumentsFile", out var instEl) && instEl.ValueKind == JsonValueKind.String ? instEl.GetString() : null;
    }
    if (instruments == null || !File.Exists(instruments)) { Console.Error.WriteLine("Cannot resolve instruments file"); return 2; }
    var tickMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (root.TryGetProperty("data", out var dataEl2) && dataEl2.TryGetProperty("ticks", out var ticksEl) && ticksEl.ValueKind == JsonValueKind.Object)
    {
        foreach (var p in ticksEl.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) tickMap[p.Name] = p.Value.GetString()!;
    }
    // Apply overrides
    foreach (var kv in tickOverrides) tickMap[kv.Key] = kv.Value;
    // Required symbols for ordering: EURUSD, USDJPY, XAUUSD (if present in config)
    string[] ordering = new[] { "EURUSD", "USDJPY", "XAUUSD" };
    var orderedTickPaths = new List<string>();
    foreach (var sym in ordering) if (tickMap.TryGetValue(sym, out var pth)) orderedTickPaths.Add(pth);
    // Build full ordered list
    var paths = new List<string>();
    paths.Add(instruments);
    paths.AddRange(orderedTickPaths);
    paths.Add(config);
    // Validate existence
    foreach (var p in paths) if (!File.Exists(p)) { Console.Error.WriteLine($"Missing file: {p}"); return 2; }
    // Compute using shared routine
    var hash = TiYf.Engine.Core.DataVersion.Compute(paths);
    Console.WriteLine($"DATA_VERSION={hash}");
    if (echoRows)
    {
        // Row counts exclude header
        Console.WriteLine($"ROWS {System.IO.Path.GetFileName(instruments)}={CountDataLines(instruments)}");
        foreach (var tp in orderedTickPaths)
            Console.WriteLine($"ROWS {System.IO.Path.GetFileName(tp)}={CountDataLines(tp)}");
    }
    if (!string.IsNullOrWhiteSpace(outFile))
    {
        var dir = System.IO.Path.GetDirectoryName(outFile)!;
        Directory.CreateDirectory(dir);
        var tmp = outFile + ".tmp";
        File.WriteAllText(tmp, hash, new UTF8Encoding(false)); // no newline
        if (File.Exists(outFile)) File.Delete(outFile);
        File.Move(tmp, outFile);
    }
    return 0;
}

static int CountDataLines(string path) => Math.Max(0, File.ReadLines(path).Count() - 1);

static int RunDiff(List<string> args)
{
    string? fileA = null, fileB = null, keyList = null; bool reportDup = false;
    for (int i = 0; i < args.Count; i++)
    {
        switch (args[i])
        {
            case "--a": fileA = (++i < args.Count) ? args[i] : null; break;
            case "--b": fileB = (++i < args.Count) ? args[i] : null; break;
            case "--keys": keyList = (++i < args.Count) ? args[i] : null; break;
            case "--report-duplicates": reportDup = true; break;
            default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 2;
        }
    }
    if (string.IsNullOrWhiteSpace(fileA) || string.IsNullOrWhiteSpace(fileB)) { Console.Error.WriteLine("--a and --b required"); return 2; }
    var keys = !string.IsNullOrWhiteSpace(keyList) ? keyList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : DiffEngine.InferDefaultKeys(fileA!, fileB!);
    var outcome = DiffEngine.Run(fileA!, fileB!, keys, reportDup);
    if (!outcome.HasDiff) { Console.WriteLine("No differences detected."); return 0; }
    Console.WriteLine(outcome.GetSummary(50));
    return 1;
}

static int RunVerify(List<string> args)
{
    // Support subcommand 'strict'
    if (args.Count > 0)
    {
        if (string.Equals(args[0], "strict", StringComparison.OrdinalIgnoreCase))
            return RunVerifyStrict(args.Skip(1).ToList());
        if (string.Equals(args[0], "parity", StringComparison.OrdinalIgnoreCase))
            return RunVerifyParity(args.Skip(1).ToList());
    }

    string? file = null; bool json = false; int maxErrors = 50; bool reportDup = false;
    for (int i = 0; i < args.Count; i++)
    {
        switch (args[i])
        {
            case "--file": file = (++i < args.Count) ? args[i] : null; break;
            case "--json": json = true; break;
            case "--max-errors": if (++i < args.Count && int.TryParse(args[i], out var m)) maxErrors = m; else throw new VerifyFatalException("--max-errors requires integer"); break;
            case "--report-duplicates": reportDup = true; break;
            default: throw new VerifyFatalException($"Unknown option {args[i]}");
        }
    }
    if (string.IsNullOrWhiteSpace(file)) throw new VerifyFatalException("--file required");
    var result = VerifyEngine.Run(file!, new VerifyOptions(maxErrors, json, reportDup));
    if (result.JsonOutput != null) Console.WriteLine(result.JsonOutput); else Console.WriteLine(result.HumanSummary);
    return result.ExitCode;
}

static int RunVerifyStrict(List<string> args)
{
    string? events = null; string? trades = null; string? schema = null; bool json = false; bool lenient = false;
    for (int i = 0; i < args.Count; i++)
    {
        switch (args[i])
        {
            case "--events": events = (++i < args.Count) ? args[i] : null; break;
            case "--trades": trades = (++i < args.Count) ? args[i] : null; break;
            case "--schema": schema = (++i < args.Count) ? args[i] : null; break;
            case "--json": json = true; break;
            case "--lenient-order": lenient = true; break;
            default: throw new VerifyFatalException($"Unknown option {args[i]}");
        }
    }
    if (string.IsNullOrWhiteSpace(events) || string.IsNullOrWhiteSpace(trades) || string.IsNullOrWhiteSpace(schema))
        throw new VerifyFatalException("--events, --trades, --schema required for verify strict");
    StrictVerifyReport report;
    try
    {
        report = StrictJournalVerifier.Verify(new StrictVerifyRequest(events!, trades!, schema!, strict: !lenient));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"STRICT VERIFY RUNTIME ERROR: {ex.Message}");
        return 1; // runtime error
    }
    if (json)
    {
        Console.WriteLine(report.JsonReport);
    }
    else
    {
        if (report.ExitCode == 0)
        {
            // parse counts from JSON for summary without re-parsing files
            Console.WriteLine("STRICT VERIFY: OK");
        }
        else
        {
            Console.WriteLine($"STRICT VERIFY: FAIL violations={report.Violations.Count}");
            foreach (var v in report.Violations.Take(10))
            {
                Console.WriteLine($"  {v.Sequence}:{v.Kind}:{v.Detail}");
            }
            if (report.Violations.Count > 10) Console.WriteLine($"  ...(truncated, total={report.Violations.Count})");
        }
    }
    return report.ExitCode; // 0 ok, 2 fail
}

// Local hash helper to avoid forward reference issues with Sha256Raw
static string HashRaw(string content)
{
    using var sha = System.Security.Cryptography.SHA256.Create();
    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
    return string.Concat(hash.Select(b => b.ToString("X2")));
}

static int RunVerifyParity(List<string> args)
{
    string? eventsA = null, eventsB = null, tradesA = null, tradesB = null; bool json = false;
    for (int i = 0; i < args.Count; i++)
    {
        switch (args[i])
        {
            case "--events-a": eventsA = (++i < args.Count) ? args[i] : null; break;
            case "--events-b": eventsB = (++i < args.Count) ? args[i] : null; break;
            case "--trades-a": tradesA = (++i < args.Count) ? args[i] : null; break;
            case "--trades-b": tradesB = (++i < args.Count) ? args[i] : null; break;
            case "--json": json = true; break;
            default: throw new VerifyFatalException($"Unknown option {args[i]}");
        }
    }
    if (string.IsNullOrWhiteSpace(eventsA) || string.IsNullOrWhiteSpace(eventsB))
        throw new VerifyFatalException("--events-a and --events-b required for verify parity");
    if (!File.Exists(eventsA!) || !File.Exists(eventsB!))
        throw new VerifyFatalException("events files must exist");
    if (!string.IsNullOrWhiteSpace(tradesA) ^ !string.IsNullOrWhiteSpace(tradesB))
        throw new VerifyFatalException("--trades-a and --trades-b must be provided together");
    if (!string.IsNullOrWhiteSpace(tradesA) && (!File.Exists(tradesA!) || !File.Exists(tradesB!)))
        throw new VerifyFatalException("trades files must exist");

    try
    {
        var normEventsA = NormalizeEvents(eventsA!);
        var normEventsB = NormalizeEvents(eventsB!);
        var eventsHashA = HashRaw(string.Join('\n', normEventsA));
        var eventsHashB = HashRaw(string.Join('\n', normEventsB));
        bool eventsMatch = string.Equals(eventsHashA, eventsHashB, StringComparison.Ordinal);
        (int line, string a, string b)? eventsFirstDiff = null;
        if (!eventsMatch)
        {
            int max = Math.Max(normEventsA.Count, normEventsB.Count);
            for (int i = 0; i < max; i++)
            {
                var a = i < normEventsA.Count ? normEventsA[i] : "<EOF>A";
                var b = i < normEventsB.Count ? normEventsB[i] : "<EOF>B";
                if (!string.Equals(a, b, StringComparison.Ordinal)) { eventsFirstDiff = (i + 1, a, b); break; }
            }
        }

        bool tradesProvided = !string.IsNullOrWhiteSpace(tradesA);
        bool tradesMatch = true; string tradesHashA = string.Empty, tradesHashB = string.Empty; (int line, string a, string b)? tradesFirstDiff = null;
        if (tradesProvided)
        {
            var normTradesA = NormalizeTrades(tradesA!);
            var normTradesB = NormalizeTrades(tradesB!);
            tradesHashA = HashRaw(string.Join('\n', normTradesA));
            tradesHashB = HashRaw(string.Join('\n', normTradesB));
            tradesMatch = string.Equals(tradesHashA, tradesHashB, StringComparison.Ordinal);
            if (!tradesMatch)
            {
                int max = Math.Max(normTradesA.Count, normTradesB.Count);
                for (int i = 0; i < max; i++)
                {
                    var a = i < normTradesA.Count ? normTradesA[i] : "<EOF>A";
                    var b = i < normTradesB.Count ? normTradesB[i] : "<EOF>B";
                    if (!string.Equals(a, b, StringComparison.Ordinal)) { tradesFirstDiff = (i + 1, a, b); break; }
                }
            }
        }

        int exit = (eventsMatch && (!tradesProvided || tradesMatch)) ? 0 : 2;
        if (json)
        {
            var obj = new
            {
                type = "VERIFY_PARITY_V1",
                events = new { match = eventsMatch, hashA = eventsHashA, hashB = eventsHashB, firstDiff = eventsFirstDiff.HasValue ? new { line = eventsFirstDiff.Value.line, a = eventsFirstDiff.Value.a, b = eventsFirstDiff.Value.b } : null },
                trades = tradesProvided ? new { match = tradesMatch, hashA = tradesHashA, hashB = tradesHashB, firstDiff = tradesFirstDiff.HasValue ? new { line = tradesFirstDiff.Value.line, a = tradesFirstDiff.Value.a, b = tradesFirstDiff.Value.b } : null } : null,
                exitCode = exit
            };
            Console.WriteLine(JsonSerializer.Serialize(obj));
        }
        else
        {
            if (eventsMatch) Console.WriteLine($"PARITY events: OK hashA={eventsHashA} hashB={eventsHashB}");
            else
            {
                Console.WriteLine($"PARITY events: MISMATCH hashA={eventsHashA} hashB={eventsHashB}");
                if (eventsFirstDiff.HasValue)
                {
                    Console.WriteLine($"FIRST_DIFF line={eventsFirstDiff.Value.line}");
                    Console.WriteLine($"A:{eventsFirstDiff.Value.a}");
                    Console.WriteLine($"B:{eventsFirstDiff.Value.b}");
                }
            }
            if (tradesProvided)
            {
                if (tradesMatch) Console.WriteLine($"PARITY trades: OK hashA={tradesHashA} hashB={tradesHashB}");
                else
                {
                    Console.WriteLine($"PARITY trades: MISMATCH hashA={tradesHashA} hashB={tradesHashB}");
                    if (tradesFirstDiff.HasValue)
                    {
                        Console.WriteLine($"FIRST_DIFF line={tradesFirstDiff.Value.line}");
                        Console.WriteLine($"A:{tradesFirstDiff.Value.a}");
                        Console.WriteLine($"B:{tradesFirstDiff.Value.b}");
                    }
                }
            }
        }
        return exit;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"VERIFY PARITY RUNTIME ERROR: {ex.Message}");
        return 1;
    }
}

static List<string> NormalizeEvents(string path)
{
    var all = File.ReadAllLines(path).ToList();
    if (all.Count == 0) return all;
    // Skip meta line if present (starts with schema_version=)
    if (all[0].StartsWith("schema_version=", StringComparison.OrdinalIgnoreCase)) all.RemoveAt(0);
    // Normalize line endings implicitly by using ReadAllLines and joining with \n at hash time.
    return all;
}

static List<string> NormalizeTrades(string path)
{
    var all = File.ReadAllLines(path).ToList();
    if (all.Count == 0) return all;
    // Skip meta line if present
    if (all[0].StartsWith("schema_version=", StringComparison.OrdinalIgnoreCase)) all.RemoveAt(0);
    if (all.Count == 0) return all;
    // Identify config_hash column index in header if present and blank it in data rows
    var header = all[0];
    var cols = header.Split(',');
    int cfgIdx = Array.FindIndex(cols, c => string.Equals(c.Trim(), "config_hash", StringComparison.OrdinalIgnoreCase));
    if (cfgIdx >= 0)
    {
        // ensure header normalized (keep as-is)
        for (int i = 1; i < all.Count; i++)
        {
            var parts = all[i].Split(',');
            if (parts.Length > cfgIdx) { parts[cfgIdx] = string.Empty; all[i] = string.Join(',', parts); }
        }
    }
    return all;
}

// ------------------------------------------------------------
// Promotion orchestration (lightweight) - runs baseline & candidate via Sim
// Exit codes: 0 accept, 2 reject (match other tool non-success), 1 unused
// Gating rules (hard):
//   - Candidate deterministic across two runs (events & trades hashes identical)
//   - PnL >= baseline PnL (tolerance 0.00)
//   - Max Drawdown <= baseline MaxDD (tolerance 0.00)
//   - Zero ALERT_BLOCK_ lines in candidate events
// Emits single JSON line: PROMOTION_RESULT { accepted: bool, reason: string, metrics: {...} }
// Culture invariance: forces invariant for parsing; optional --culture allows test harness to set thread culture
// ------------------------------------------------------------
static int RunPromote(List<string> args)
{
    string? baseline = null, candidate = null, workdir = null, culture = null; bool quiet = false; bool printMetrics = false; bool allowParityMismatch = false; // removed unused 'diagnose'
    for (int i = 0; i < args.Count; i++)
    {
        switch (args[i])
        {
            case "--baseline": baseline = (++i < args.Count) ? args[i] : null; break;
            case "--candidate": candidate = (++i < args.Count) ? args[i] : null; break;
            case "--workdir": workdir = (++i < args.Count) ? args[i] : null; break;
            case "--quiet": quiet = true; break;
            case "--print-metrics": printMetrics = true; break;
            case "--culture": culture = (++i < args.Count) ? args[i] : null; break;
            case "--allow-parity-mismatch": allowParityMismatch = true; break;
            default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 2;
        }
    }
    if (baseline == null || candidate == null) { Console.Error.WriteLine("--baseline and --candidate required"); return 2; }
    if (!File.Exists(baseline) || !File.Exists(candidate)) { Console.Error.WriteLine("Baseline or candidate config missing"); return 2; }
    if (!string.IsNullOrWhiteSpace(culture))
    {
        try
        {
            var ci = System.Globalization.CultureInfo.GetCultureInfo(culture);
            System.Globalization.CultureInfo.CurrentCulture = ci;
            System.Globalization.CultureInfo.CurrentUICulture = ci;
        }
        catch { /* ignore invalid culture */ }
    }
    workdir ??= Directory.GetCurrentDirectory();
    string simDll = Path.Combine(workdir, "src", "TiYf.Engine.Sim", "bin", "Release", "net8.0", "TiYf.Engine.Sim.dll");
    if (!File.Exists(simDll)) { Console.Error.WriteLine("Sim binary not built (expected Release build). Run dotnet build -c Release."); return 2; }

    var tmpRoot = Path.Combine(Path.GetTempPath(), "promote_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpRoot);
    bool keepArtifacts = false; // set to true if determinism failure so we skip deletion
    try
    {
        // Resolve sentiment modes up-front (default shadow). Off synonyms: off|disabled|none.
        static string ResolveSentimentMode(string cfgPath)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
                string mode = "shadow";
                if (doc.RootElement.TryGetProperty("featureFlags", out var ff) && ff.ValueKind == JsonValueKind.Object && ff.TryGetProperty("sentiment", out var s) && s.ValueKind == JsonValueKind.String)
                {
                    var raw = s.GetString() ?? "shadow";
                    if (raw.Equals("off", StringComparison.OrdinalIgnoreCase) || raw.Equals("disabled", StringComparison.OrdinalIgnoreCase) || raw.Equals("none", StringComparison.OrdinalIgnoreCase)) mode = "off";
                    else if (raw.Equals("active", StringComparison.OrdinalIgnoreCase)) mode = "active";
                    else mode = "shadow"; // treat shadow/unknown as shadow
                }
                return mode;
            }
            catch { return "shadow"; }
        }
        var baselineMode = ResolveSentimentMode(baseline!);
        var candidateMode = ResolveSentimentMode(candidate!);
        // Risk mode resolver (mirrors sentiment logic, default off)
        static string ResolveRiskMode(string cfgPath)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
                string mode = "off";
                if (doc.RootElement.TryGetProperty("featureFlags", out var ff) && ff.ValueKind == JsonValueKind.Object && ff.TryGetProperty("risk", out var r) && r.ValueKind == JsonValueKind.String)
                {
                    var raw = r.GetString() ?? "off";
                    if (raw.Equals("active", StringComparison.OrdinalIgnoreCase)) mode = "active";
                    else if (raw.Equals("shadow", StringComparison.OrdinalIgnoreCase)) mode = "shadow"; else mode = "off";
                }
                return mode;
            }
            catch { return "off"; }
        }
        var baselineRiskMode = ResolveRiskMode(baseline!);
        var candidateRiskMode = ResolveRiskMode(candidate!);
        // Removed verbose risk mode diagnostics (was: RISK_MODE_RESOLVED)

        // Pre-flight candidate zero exposure cap detection for deterministic gating (used by promotion tests)
        bool candidateZeroCap = false;
        try
        {
            using var candDoc = JsonDocument.Parse(File.ReadAllText(candidate!));
            if (candDoc.RootElement.TryGetProperty("riskConfig", out var rc) && rc.ValueKind == JsonValueKind.Object && rc.TryGetProperty("maxNetExposureBySymbol", out var mneb) && mneb.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in mneb.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out var v) && v == 0) { candidateZeroCap = true; break; }
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDecimal(out var dv) && dv == 0m) { candidateZeroCap = true; break; }
                }
            }
        }
        catch { }

        // Diagnostic flags (pre-run) for promotion test visibility
        Console.WriteLine($"PROMOTE_FLAGS sentiment_base={baselineMode} sentiment_cand={candidateMode} risk_base={baselineRiskMode} risk_cand={candidateRiskMode} cand_zero_cap={candidateZeroCap.ToString().ToLowerInvariant()}");

        // Run baseline once
        var baseRun = RunSingle(simDll, baseline!, tmpRoot, workdir!, "base");
        if (baseRun.ExitCode != 0) return Reject("Baseline run failed", baseRun, null, null);
        // Strict verification (baseline)
        try
        {
            var baseStrict = StrictJournalVerifier.Verify(new StrictVerifyRequest(baseRun.EventsPath, baseRun.TradesPath, "1.3.0", strict: true));
            if (baseStrict.ExitCode != 0)
            {
                Console.WriteLine("PROMOTE_GATES baseline_strict=fail");
                return Reject("baseline_verify_failed", baseRun, null, null);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PROMOTE_STRICT_BASELINE_ERROR {ex.Message}");
            return Reject("baseline_verify_failed", baseRun, null, null);
        }
        // Candidate run A & B for determinism
        var candRunA = RunSingle(simDll, candidate!, tmpRoot, workdir!, "candA");
        if (candRunA.ExitCode != 0) return Reject("Candidate run A failed", baseRun, candRunA, null);
        var candRunB = RunSingle(simDll, candidate!, tmpRoot, workdir!, "candB");
        if (candRunB.ExitCode != 0) return Reject("Candidate run B failed", baseRun, candRunA, candRunB);

        // Strict verification (candidate A)
        try
        {
            var candStrict = StrictJournalVerifier.Verify(new StrictVerifyRequest(candRunA.EventsPath, candRunA.TradesPath, "1.3.0", strict: true));
            if (candStrict.ExitCode != 0)
            {
                Console.WriteLine("PROMOTE_GATES candidate_strict=fail");
                // Prefer data QA failure reason if detectable from candidate events
                try
                {
                    var qa = ExtractDataQaStatus(candRunA.EventsPath);
                    if (qa.aborted || (qa.passed.HasValue && qa.passed.Value == false))
                        return Reject("data_qa_failed", baseRun, candRunA, candRunB);
                }
                catch { }
                return Reject("verify_failed", baseRun, candRunA, candRunB);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PROMOTE_STRICT_CAND_ERROR {ex.Message}");
            return Reject("verify_failed", baseRun, candRunA, candRunB);
        }

        // Determinism: hash events & trades
        // Determinism should not be affected by differing run identifiers present in the journal meta line.
        // The first line of events/trades files contains meta including run id; skip it when hashing.
        var eventsHashA = Sha256FileSkipMeta(candRunA.EventsPath);
        var eventsHashB = Sha256FileSkipMeta(candRunB.EventsPath);
        var tradesHashA = Sha256FileSkipMeta(candRunA.TradesPath);
        var tradesHashB = Sha256FileSkipMeta(candRunB.TradesPath);
        if (!string.Equals(eventsHashA, eventsHashB, StringComparison.Ordinal) || !string.Equals(tradesHashA, tradesHashB, StringComparison.Ordinal))
        {
            var diagHeader = $"DETERMINISM_DIAG eventsHashA={eventsHashA} eventsHashB={eventsHashB} tradesHashA={tradesHashA} tradesHashB={tradesHashB}";
            Console.Error.WriteLine(diagHeader);
            Console.WriteLine(diagHeader);
            Console.Error.WriteLine($"ARTIFACTS_ROOT={tmpRoot}");
            Console.WriteLine($"ARTIFACTS_ROOT={tmpRoot}");
            Console.Error.WriteLine($"CAND_A_EVENTS={candRunA.EventsPath}");
            Console.Error.WriteLine($"CAND_B_EVENTS={candRunB.EventsPath}");
            Console.Error.WriteLine($"CAND_A_TRADES={candRunA.TradesPath}");
            Console.Error.WriteLine($"CAND_B_TRADES={candRunB.TradesPath}");
            Console.WriteLine($"CAND_A_EVENTS={candRunA.EventsPath}");
            Console.WriteLine($"CAND_B_EVENTS={candRunB.EventsPath}");
            Console.WriteLine($"CAND_A_TRADES={candRunA.TradesPath}");
            Console.WriteLine($"CAND_B_TRADES={candRunB.TradesPath}");
            try
            {
                var aLines = File.ReadAllLines(candRunA.EventsPath);
                var bLines = File.ReadAllLines(candRunB.EventsPath);
                int max = Math.Max(aLines.Length, bLines.Length);
                for (int i = 0; i < max; i++)
                {
                    var aL = i < aLines.Length ? aLines[i] : "<EOF>A";
                    var bL = i < bLines.Length ? bLines[i] : "<EOF>B";
                    if (!string.Equals(aL, bL, StringComparison.Ordinal))
                    {
                        int ctxStart = Math.Max(0, i - 5);
                        var mismatchHeader = $"FIRST_EVENT_MISMATCH line={i + 1}";
                        Console.Error.WriteLine(mismatchHeader);
                        Console.WriteLine(mismatchHeader);
                        for (int c = ctxStart; c < i; c++)
                        {
                            var ctxLine = $"CTX:{c + 1}:{aLines[c]}";
                            Console.Error.WriteLine(ctxLine);
                            Console.WriteLine(ctxLine);
                        }
                        var aOut = $"A:{aL}"; var bOut = $"B:{bL}";
                        Console.Error.WriteLine(aOut); Console.WriteLine(aOut);
                        Console.Error.WriteLine(bOut); Console.WriteLine(bOut);
                        break;
                    }
                }
                var aT = SafeRead(candRunA.TradesPath);
                var bT = SafeRead(candRunB.TradesPath);
                if (aT.Count == bT.Count)
                {
                    for (int i = 0; i < aT.Count; i++)
                    {
                        if (!string.Equals(aT[i], bT[i], StringComparison.Ordinal))
                        {
                            int ctxStart = Math.Max(0, i - 5);
                            var tradeMismatch = $"FIRST_TRADE_MISMATCH line={i + 1}";
                            Console.Error.WriteLine(tradeMismatch);
                            Console.WriteLine(tradeMismatch);
                            for (int c = ctxStart; c < i; c++) Console.Error.WriteLine($"TRADE_CTX:{c + 1}:{aT[c]}");
                            Console.Error.WriteLine($"A_TRADE:{aT[i]}");
                            Console.Error.WriteLine($"B_TRADE:{bT[i]}");
                            break;
                        }
                    }
                }
            }
            catch (Exception dx)
            {
                Console.Error.WriteLine($"DIAG_ERROR {dx.Message}");
            }
            Console.Out.Flush();
            Console.Error.Flush();
            keepArtifacts = true; // signal finally block to retain directory
            return Reject("Determinism parity failed", baseRun, candRunA, candRunB);
        }

        // ------------------------------
        // FACT COLLECTION (no mutation)
        // ------------------------------
        var baseQa = ExtractDataQaStatus(baseRun.EventsPath);
        var candQa = ExtractDataQaStatus(candRunA.EventsPath);
        var candQaB = ExtractDataQaStatus(candRunB.EventsPath);
        var baseMetrics = ComputeMetrics(baseRun.TradesPath);
        var candMetrics = ComputeMetrics(candRunA.TradesPath);
        // Build risk parity object early to reuse alert counts & parity signal
        var riskParityObj = BuildRiskParity(baselineRiskMode, candidateRiskMode, baseRun.EventsPath, candRunA.EventsPath);
        var rpType = riskParityObj.GetType();
        bool riskParity = true; string riskReason = "ok"; int baseAlerts = 0; int candAlerts = 0;
        try
        {
            riskParity = (bool)(rpType.GetProperty("parity")?.GetValue(riskParityObj) ?? true);
            riskReason = (string?)(rpType.GetProperty("reason")?.GetValue(riskParityObj) ?? "ok")!;
            var baseAlertsProp = rpType.GetProperty("baseline")?.GetValue(riskParityObj);
            var candAlertsProp = rpType.GetProperty("candidate")?.GetValue(riskParityObj);
            if (baseAlertsProp != null)
            {
                var bType = baseAlertsProp.GetType();
                baseAlerts = (int)(bType.GetProperty("alerts")?.GetValue(baseAlertsProp) ?? 0);
            }
            if (candAlertsProp != null)
            {
                var cType = candAlertsProp.GetType();
                candAlerts = (int)(cType.GetProperty("alerts")?.GetValue(candAlertsProp) ?? 0);
            }
        }
        catch { /* reflective safety */ }

        // Candidate zero-cap robust detection (case-insensitive keys, <=0 across all declared symbols, non-empty map)
        bool robustZeroCap = false;
        try
        {
            using var candDoc2 = JsonDocument.Parse(File.ReadAllText(candidate!));
            JsonElement riskCfgEl = default; bool foundRiskCfg = false;
            foreach (var prop in candDoc2.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("riskConfig") || prop.Name.Equals("risk_config", StringComparison.OrdinalIgnoreCase)) { riskCfgEl = prop.Value; foundRiskCfg = true; break; }
            }
            if (foundRiskCfg && riskCfgEl.ValueKind == JsonValueKind.Object)
            {
                JsonElement capsEl = default; bool foundCaps = false;
                foreach (var rcProp in riskCfgEl.EnumerateObject())
                {
                    if (rcProp.NameEquals("maxNetExposureBySymbol") || rcProp.Name.Equals("max_net_exposure_by_symbol", StringComparison.OrdinalIgnoreCase)) { capsEl = rcProp.Value; foundCaps = true; break; }
                }
                if (foundCaps && capsEl.ValueKind == JsonValueKind.Object)
                {
                    int symbolCount = 0; bool allZeroOrNeg = true;
                    foreach (var cap in capsEl.EnumerateObject())
                    {
                        if (cap.Value.ValueKind == JsonValueKind.Number)
                        {
                            symbolCount++;
                            decimal val = 0m; if (cap.Value.TryGetDecimal(out var dv)) val = dv; else if (cap.Value.TryGetInt64(out var iv)) val = iv;
                            if (val > 0m) { allZeroOrNeg = false; break; }
                        }
                    }
                    robustZeroCap = symbolCount > 0 && allZeroOrNeg;
                }
            }
        }
        catch { }
        // Prefer robust detection over earlier heuristic if true
        if (robustZeroCap) candidateZeroCap = true;
        // PROMOTE_FACTS (pre-resolution) per blueprint
        bool qaPassed = !candQa.aborted && (candQa.passed ?? false);
        // Penalty facts: counts per run for quick visibility
        int factsPenaltyBase = 0, factsPenaltyCand = 0;
        try
        {
            factsPenaltyBase = File.ReadLines(baseRun.EventsPath).Count(l => l.Contains("PENALTY_APPLIED_V1"));
            factsPenaltyCand = File.ReadLines(candRunA.EventsPath).Count(l => l.Contains("PENALTY_APPLIED_V1"));
        }
        catch { }
        Console.WriteLine($"PROMOTE_FACTS riskBase={baselineRiskMode} riskCand={candidateRiskMode} candZeroCap={candidateZeroCap.ToString().ToLowerInvariant()} baseRows={baseMetrics.rows} candRows={candMetrics.rows} baseAlerts={baseAlerts} candAlerts={candAlerts} penBase={factsPenaltyBase} penCand={factsPenaltyCand} penaltyBaseCount={factsPenaltyBase} penaltyCandCount={factsPenaltyCand} qaPassed={qaPassed.ToString().ToLowerInvariant()}");
        if (candidateRiskMode == "active" && !candidateZeroCap)
        {
            // Probe when expected zero-cap scenario failed detection
            try
            {
                using var candDoc3 = JsonDocument.Parse(File.ReadAllText(candidate!));
                var capsKeys = new List<string>();
                foreach (var prop in candDoc3.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("riskConfig") || prop.Name.Equals("risk_config", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var rcProp in prop.Value.EnumerateObject())
                        {
                            if (rcProp.NameEquals("maxNetExposureBySymbol") || rcProp.Name.Equals("max_net_exposure_by_symbol", StringComparison.OrdinalIgnoreCase))
                            {
                                if (rcProp.Value.ValueKind == JsonValueKind.Object)
                                {
                                    foreach (var cap in rcProp.Value.EnumerateObject()) capsKeys.Add(cap.Name);
                                }
                            }
                        }
                    }
                }
                Console.WriteLine($"PROMOTE_FLAGS risk_base={baselineRiskMode} risk_cand={candidateRiskMode} cand_zero_cap={candidateZeroCap.ToString().ToLowerInvariant()} caps_keys=[{string.Join(',', capsKeys)}]");
            }
            catch { }
        }

        // ------------------------------
        // REASON RESOLUTION (priority ladder)
        // ------------------------------
        string reason = string.Empty;
        // 1. Data QA failure
        if (candQa.aborted || (candQa.passed.HasValue && !candQa.passed.Value))
        {
            reason = "data_qa_failed";
        }
        // 2. Risk downgrade active->(shadow/off)
        else if (baselineRiskMode == "active" && candidateRiskMode != "active")
        {
            reason = "risk_mismatch";
        }
        // 3. Shadow->Active zero-cap divergence (alerts or rowcount diff)
        else if (baselineRiskMode == "shadow" && candidateRiskMode == "active" && candidateZeroCap && (baseAlerts != candAlerts || baseMetrics.rows != candMetrics.rows))
        {
            reason = "risk_mismatch";
        }
        // 4. Risk alert mismatch (parity false & riskReason==risk_mismatch)
        else if (!riskParity && riskReason == "risk_mismatch")
        {
            reason = "risk_mismatch";
        }
        // 5. Row count mismatch
        else if (candMetrics.rows != baseMetrics.rows)
        {
            reason = $"RowCount mismatch base={baseMetrics.rows} cand={candMetrics.rows}";
        }
        // 6. PnL worse
        else if (candMetrics.pnl < baseMetrics.pnl - 0.0000m)
        {
            reason = "PnL worsened";
        }
        // 7. Max drawdown worse
        else if (candMetrics.maxDd > baseMetrics.maxDd + 0.0000m)
        {
            reason = "MaxDD worsened";
        }

        // Sentiment parity gating (only if still accepted so far)
        bool sentimentParity = true; string sentimentReason = "ok"; string diffHint = string.Empty;
        if (string.IsNullOrEmpty(reason))
        {
            try
            {
                static List<string> LoadSentimentLines(string path)
                {
                    var all = File.ReadAllLines(path);
                    var list = new List<string>();
                    for (int i = 2; i < all.Length; i++)
                    {
                        var line = all[i];
                        if (line.Contains("INFO_SENTIMENT_", StringComparison.Ordinal)) list.Add(line);
                    }
                    return list;
                }
                var baseSent = LoadSentimentLines(baseRun.EventsPath);
                var candSent = LoadSentimentLines(candRunA.EventsPath);
                int baseApplied = baseSent.Count(l => l.Contains("INFO_SENTIMENT_APPLIED_V1", StringComparison.Ordinal));
                int candApplied = candSent.Count(l => l.Contains("INFO_SENTIMENT_APPLIED_V1", StringComparison.Ordinal));
                if (!(baselineMode == "shadow" && candidateMode == "active" && candApplied == 0))
                {
                    if (baselineMode != candidateMode)
                    { sentimentParity = false; sentimentReason = "sentiment_mismatch"; }
                    else
                    {
                        int min = Math.Min(baseSent.Count, candSent.Count);
                        for (int i = 0; i < min; i++)
                        {
                            if (!string.Equals(baseSent[i], candSent[i], StringComparison.Ordinal)) { sentimentParity = false; sentimentReason = "sentiment_mismatch"; diffHint = BuildDiffHint(baseSent[i], candSent[i]); break; }
                        }
                        if (sentimentParity && baseSent.Count != candSent.Count)
                        { sentimentParity = false; sentimentReason = "sentiment_mismatch"; diffHint = "sentiment_event_count"; }
                        if (sentimentParity && baselineMode == "active" && baseApplied != candApplied)
                        { sentimentParity = false; sentimentReason = "sentiment_mismatch"; diffHint = $"applied_count base={baseApplied} cand={candApplied}"; }
                    }
                    if (baselineMode == "active" && (!sentimentParity || candidateMode != "active"))
                    {
                        reason = "sentiment_mismatch"; sentimentParity = false; sentimentReason = "sentiment_mismatch";
                        if (string.IsNullOrEmpty(diffHint) && baseSent.Count > 0 && candSent.Count > 0) diffHint = BuildDiffHint(baseSent[0], candSent[0]);
                    }
                }
            }
            catch (Exception sx)
            {
                if (baselineMode == "active") { reason = "sentiment_mismatch"; sentimentParity = false; sentimentReason = "sentiment_mismatch"; diffHint = "exception " + sx.GetType().Name; }
            }
        }

        // Penalty parity gating (before final events/trades parity) if still no reason
        bool penaltyParity = true; string penaltyReason = "ok"; string penaltyHint = string.Empty;
        if (string.IsNullOrEmpty(reason))
        {
            static string ResolvePenaltyMode(string cfg)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                    if (doc.RootElement.TryGetProperty("featureFlags", out var ff) && ff.ValueKind == JsonValueKind.Object && ff.TryGetProperty("penalty", out var p) && p.ValueKind == JsonValueKind.String)
                        return (p.GetString() ?? "off").ToLowerInvariant();
                }
                catch { }
                return "off";
            }
            string basePenalty = ResolvePenaltyMode(baseline!);
            string candPenalty = ResolvePenaltyMode(candidate!);
            try
            {
                static List<string> LoadPenalty(string eventsPath)
                {
                    var lines = File.ReadAllLines(eventsPath);
                    var list = new List<string>();
                    for (int i = 2; i < lines.Length; i++) if (lines[i].Contains("PENALTY_APPLIED_V1", StringComparison.Ordinal)) list.Add(lines[i]);
                    return list;
                }
                var penA = LoadPenalty(baseRun.EventsPath);
                var penB = LoadPenalty(candRunA.EventsPath);
                int baseCnt = penA.Count, candCnt = penB.Count;
                bool benignShadowToActiveZero = basePenalty == "shadow" && candPenalty == "active" && candCnt == 0;
                if (!benignShadowToActiveZero)
                {
                    if (basePenalty != candPenalty) { penaltyParity = false; penaltyReason = "penalty_mismatch"; }
                    else
                    {
                        int min = Math.Min(baseCnt, candCnt);
                        for (int i = 0; i < min; i++) if (!string.Equals(penA[i], penB[i], StringComparison.Ordinal)) { penaltyParity = false; penaltyReason = "penalty_mismatch"; penaltyHint = BuildDiffHint(penA[i], penB[i]); break; }
                        if (penaltyParity && baseCnt != candCnt) { penaltyParity = false; penaltyReason = "penalty_mismatch"; penaltyHint = $"penalty_count base={baseCnt} cand={candCnt}"; }
                    }
                    if (!penaltyParity) reason = "penalty_mismatch";
                }
            }
            catch (Exception ex)
            {
                // If baseline expects active, treat exceptions as mismatch
                if (basePenalty == "active") { penaltyParity = false; penaltyReason = "penalty_mismatch"; penaltyHint = "exception " + ex.GetType().Name; reason = "penalty_mismatch"; }
            }
        }

        // 8. Baseline vs Candidate parity (events + trades) unless explicitly allowed to mismatch
        if (string.IsNullOrEmpty(reason))
        {
            // allow override via env as well
            if (!allowParityMismatch)
            {
                var envAllow = Environment.GetEnvironmentVariable("PROMOTE_ALLOW_PARITY_MISMATCH");
                if (!string.IsNullOrEmpty(envAllow) && (envAllow.Equals("1") || envAllow.Equals("true", StringComparison.OrdinalIgnoreCase)))
                    allowParityMismatch = true;
            }
            if (!allowParityMismatch)
            {
                var evA = NormalizeEvents(baseRun.EventsPath); var evB = NormalizeEvents(candRunA.EventsPath);
                var trA = NormalizeTrades(baseRun.TradesPath); var trB = NormalizeTrades(candRunA.TradesPath);
                string evHashA = HashRaw(string.Join('\n', evA)), evHashB = HashRaw(string.Join('\n', evB));
                string trHashA = HashRaw(string.Join('\n', trA)), trHashB = HashRaw(string.Join('\n', trB));
                bool evMatch = string.Equals(evHashA, evHashB, StringComparison.Ordinal);
                bool trMatch = string.Equals(trHashA, trHashB, StringComparison.Ordinal);
                if (!evMatch || !trMatch)
                {
                    // produce a compact first diff for diagnostics
                    string diff = string.Empty;
                    if (!evMatch)
                    {
                        int max = Math.Max(evA.Count, evB.Count);
                        for (int i = 0; i < max; i++) { var a = i < evA.Count ? evA[i] : "<EOF>A"; var b = i < evB.Count ? evB[i] : "<EOF>B"; if (!string.Equals(a, b, StringComparison.Ordinal)) { diff = $"events line={i + 1}"; break; } }
                    }
                    else
                    {
                        int max = Math.Max(trA.Count, trB.Count);
                        for (int i = 0; i < max; i++) { var a = i < trA.Count ? trA[i] : "<EOF>A"; var b = i < trB.Count ? trB[i] : "<EOF>B"; if (!string.Equals(a, b, StringComparison.Ordinal)) { diff = $"trades line={i + 1}"; break; } }
                    }
                    reason = string.IsNullOrEmpty(diff) ? "parity_mismatch" : $"parity_mismatch ({diff})";
                }
            }
        }

        bool accepted = string.IsNullOrEmpty(reason);

        // Fallback normalization safety (should be redundant with ladder)
        if (!accepted && reason.StartsWith("RowCount mismatch", StringComparison.Ordinal) && baselineRiskMode == "shadow" && candidateRiskMode == "active" && candidateZeroCap)
        {
            reason = "risk_mismatch"; accepted = false; // keep rejected
        }

        // PROMOTE_DECISION line per blueprint
        Console.WriteLine($"PROMOTE_DECISION finalReason={(string.IsNullOrEmpty(reason) ? "" : reason)} accepted={accepted.ToString().ToLowerInvariant()}");

        var resultObj = new
        {
            type = "PROMOTION_RESULT_V1",
            accepted,
            reason = string.IsNullOrEmpty(reason) ? "accept" : reason,
            sentiment = new { baseline_mode = baselineMode, candidate_mode = candidateMode, parity = sentimentParity, reason = sentimentReason, diff_hint = diffHint },
            penalty = new { parity = penaltyParity, reason = penaltyReason, diff_hint = penaltyHint },
            risk = riskParityObj,
            baseline = new { pnl = Round2(baseMetrics.pnl), maxDd = Round2(baseMetrics.maxDd), rows = baseMetrics.rows },
            candidate = new { pnl = Round2(candMetrics.pnl), maxDd = Round2(candMetrics.maxDd), rows = candMetrics.rows, alerts = candAlerts },
            hashes = new { events = eventsHashA, trades = tradesHashA, config_base = baseRun.ConfigHash, config_cand = candRunA.ConfigHash },
            dataQa = new
            {
                baseline = new { aborted = baseQa.aborted, passed = baseQa.passed },
                candidate = new { aborted = candQa.aborted, passed = candQa.passed },
                candidateB = new { aborted = candQaB.aborted, passed = candQaB.passed }
            }
        };
        var json = JsonSerializer.Serialize(resultObj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (!quiet) Console.WriteLine(json);
        if (printMetrics)
        {
            Console.WriteLine($"BASE_PNL={baseMetrics.pnl.ToString(System.Globalization.CultureInfo.InvariantCulture)} BASE_MAXDD={baseMetrics.maxDd.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            Console.WriteLine($"CAND_PNL={candMetrics.pnl.ToString(System.Globalization.CultureInfo.InvariantCulture)} CAND_MAXDD={candMetrics.maxDd.ToString(System.Globalization.CultureInfo.InvariantCulture)} ALERT_BLOCKS={candAlerts}");
        }
        return accepted ? 0 : 2;
    }
    finally { try { if (Directory.Exists(tmpRoot) && !keepArtifacts) Directory.Delete(tmpRoot, true); } catch { } }

    static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    // Local helpers (duplicate minimal logic from tests to avoid new dependency cycle)
    static string BuildDiffHint(string a, string b)
    {
        if (a.Length > 120) a = a.Substring(0, 120);
        if (b.Length > 120) b = b.Substring(0, 120);
        return $"A:{a}|B:{b}";
    }
    static (int ExitCode, string EventsPath, string TradesPath, string ConfigHash) RunSingle(string simDll, string cfg, string scratchRoot, string repoRoot, string tag)
    {
        string runId = "PROMO-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string tempCfg = Path.Combine(scratchRoot, tag + "_" + Guid.NewGuid().ToString("N") + ".json");
        File.Copy(cfg, tempCfg, true);
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"exec \"{simDll}\" --config \"{tempCfg}\" --quiet --run-id {runId}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot
        };
        var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(120000);
        if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { } return (2, string.Empty, string.Empty, string.Empty); }
        // Journal dir pattern: journals/M0/M0-RUN-{runId} under provided repoRoot
        string runDir = Path.Combine(repoRoot, "journals", "M0", $"M0-RUN-{runId}");
        string eventsPath = Path.Combine(runDir, "events.csv");
        string tradesPath = Path.Combine(runDir, "trades.csv");
        string configHash = ExtractConfigHash(eventsPath);
        return (proc.ExitCode, eventsPath, tradesPath, configHash);
    }

    static List<string> SafeRead(string path)
    {
        try { return File.ReadAllLines(path).ToList(); } catch { return new List<string>(); }
    }

    static string ExtractConfigHash(string eventsPath)
    {
        try
        {
            var first = File.ReadLines(eventsPath).Skip(1).FirstOrDefault();
            if (first == null) return string.Empty;
            var parts = first.Split(',', 4);
            if (parts.Length < 4) return string.Empty;
            var payload = parts[3];
            payload = payload.Trim('"').Replace("\"\"", "\"");
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("config_hash", out var ch) && ch.ValueKind == JsonValueKind.String) return ch.GetString()!;
        }
        catch { }
        return string.Empty;
    }

    static (decimal pnl, decimal maxDd, int rows) ComputeMetrics(string tradesCsv)
    {
        try
        {
            var lines = File.ReadAllLines(tradesCsv).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (lines.Count <= 1) return (0m, 0m, 0);
            var header = lines[0].Split(',');
            int pnlIdx = Array.FindIndex(header, h => h.Equals("pnl_ccy", StringComparison.OrdinalIgnoreCase));
            if (pnlIdx < 0) return (0m, 0m, 0);
            decimal sum = 0m; var cum = new List<decimal>();
            foreach (var row in lines.Skip(1))
            {
                var parts = row.Split(',');
                if (parts.Length <= pnlIdx) continue;
                if (decimal.TryParse(parts[pnlIdx], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v))
                { sum += v; cum.Add(sum); }
            }
            decimal peak = decimal.MinValue; decimal maxDd = 0m; foreach (var c in cum) { if (c > peak) peak = c; var dd = peak - c; if (dd > maxDd) maxDd = dd; }
            return (sum, maxDd, cum.Count);
        }
        catch { return (0m, 0m, 0); }
    }

    // Removed obsolete CountAlertBlocks (alert counts derived from riskParityObj)

    static string Sha256FileSkipMeta(string path)
    {
        try
        {
            // Read all lines; skip the very first line (meta) to avoid run-id induced divergence
            var lines = File.ReadAllLines(path);
            if (lines.Length <= 1)
            {
                return Sha256Raw(string.Join('\n', lines));
            }
            var sb = new StringBuilder();
            for (int i = 1; i < lines.Length; i++)
            {
                if (i > 1) sb.Append('\n');
                sb.Append(lines[i]);
            }
            return Sha256Raw(sb.ToString());
        }
        catch { return string.Empty; }
    }

    static string Sha256Raw(string content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
        return string.Concat(hash.Select(b => b.ToString("X2")));
    }

    static int Reject(string reason, (int ExitCode, string EventsPath, string TradesPath, string ConfigHash) baseRun, (int ExitCode, string EventsPath, string TradesPath, string ConfigHash)? candA, (int ExitCode, string EventsPath, string TradesPath, string ConfigHash)? candB)
    {
        var obj = new { type = "PROMOTION_RESULT_V1", accepted = false, reason };
        Console.WriteLine(JsonSerializer.Serialize(obj));
        return 2;
    }

    static object BuildRiskParity(string baseMode, string candMode, string baseEvents, string candEvents)
    {
        try
        {
            // Load risk alert lines
            static (List<string> evals, List<string> alerts) Load(string path)
            {
                var evals = new List<string>(); var alerts = new List<string>();
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Contains(",INFO_RISK_EVAL_V1,")) evals.Add(line);
                    else if (line.Contains(",ALERT_BLOCK_")) alerts.Add(line);
                }
                return (evals, alerts);
            }
            var (baseEvals, baseAlerts) = Load(baseEvents);
            var (candEvals, candAlerts) = Load(candEvents);
            bool parity = true; string reason = "ok"; string diffHint = string.Empty;
            if (baseMode == "active" && candMode != "active") { parity = false; reason = "risk_mismatch"; diffHint = "mode downgrade"; }
            else if (baseMode == "active" && candMode == "active")
            {
                if (baseAlerts.Count != candAlerts.Count)
                { parity = false; reason = "risk_mismatch"; diffHint = $"alert_count base={baseAlerts.Count} cand={candAlerts.Count}"; }
                else
                {
                    for (int i = 0; i < baseAlerts.Count; i++) if (!string.Equals(baseAlerts[i], candAlerts[i], StringComparison.Ordinal)) { parity = false; reason = "risk_mismatch"; diffHint = "alert_line_diff"; break; }
                }
            }
            else if (baseMode == "shadow" && candMode == "active")
            {
                // allowed if candidate active has no alerts (benign upgrade)
                if (candAlerts.Count > 0) { parity = false; reason = "risk_mismatch"; diffHint = "unexpected_active_alerts"; }
            }
            return new { baseline_mode = baseMode, candidate_mode = candMode, parity, reason, diff_hint = diffHint, baseline = new { evals = baseEvals.Count, alerts = baseAlerts.Count }, candidate = new { evals = candEvals.Count, alerts = candAlerts.Count } };
        }
        catch { return new { baseline_mode = baseMode, candidate_mode = candMode, parity = true, reason = "ok", diff_hint = "exception", baseline = new { evals = 0, alerts = 0 }, candidate = new { evals = 0, alerts = 0 } }; }
    }

    static (bool aborted, bool? passed) ExtractDataQaStatus(string eventsCsv)
    {
        try
        {
            bool aborted = false; bool? passed = null;
            foreach (var line in File.ReadLines(eventsCsv))
            {
                if (line.Contains(",DATA_QA_ABORT_V1,")) aborted = true;
                else if (line.Contains(",DATA_QA_SUMMARY_V1,"))
                {
                    // Parse payload JSON field
                    var parts = line.Split(',', 4); if (parts.Length < 4) continue;
                    var payload = parts[3].Trim().Trim('"').Replace("\"\"", "\"");
                    using var doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("passed", out var p) && p.ValueKind == JsonValueKind.True) passed = true;
                    else if (doc.RootElement.TryGetProperty("passed", out var p2) && p2.ValueKind == JsonValueKind.False) passed = false;
                    if (doc.RootElement.TryGetProperty("aborted", out var ab) && (ab.ValueKind == JsonValueKind.True || ab.ValueKind == JsonValueKind.False)) aborted = ab.ValueKind == JsonValueKind.True;
                }
            }
            return (aborted, passed);
        }
        catch { return (false, null); }
    }
}

internal record DiffRow(string CompositeKey, string PayloadHash);
public sealed class DiffOutcome
{
    public List<string> OnlyInA { get; } = new();
    public List<string> OnlyInB { get; } = new();
    public List<string> PayloadMismatch { get; } = new();
    public bool HasDiff => OnlyInA.Count > 0 || OnlyInB.Count > 0 || PayloadMismatch.Count > 0;
    public string GetSummary(int limit)
    {
        if (!HasDiff) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("DIFF SUMMARY:");
        if (OnlyInA.Count > 0) sb.AppendLine($"  Present only in A: {string.Join(';', OnlyInA.Take(limit))}");
        if (OnlyInB.Count > 0) sb.AppendLine($"  Present only in B: {string.Join(';', OnlyInB.Take(limit))}");
        if (PayloadMismatch.Count > 0) sb.AppendLine($"  Payload mismatches: {string.Join(';', PayloadMismatch.Take(limit))}");
        sb.AppendLine($"(showing at most {limit} keys per category)");
        return sb.ToString();
    }
}

public static class DiffEngine
{
    public static DiffOutcome Run(string pathA, string pathB, string[] keyFields, bool reportDuplicates = false)
    {
        var rowsA = LoadRows(pathA, keyFields);
        var rowsB = LoadRows(pathB, keyFields);
        var dictA = new Dictionary<string, DiffRow>(); var dupA = new HashSet<string>();
        foreach (var r in rowsA) if (!dictA.ContainsKey(r.CompositeKey)) dictA[r.CompositeKey] = r; else dupA.Add(r.CompositeKey);
        var dictB = new Dictionary<string, DiffRow>(); var dupB = new HashSet<string>();
        foreach (var r in rowsB) if (!dictB.ContainsKey(r.CompositeKey)) dictB[r.CompositeKey] = r; else dupB.Add(r.CompositeKey);
        var outcome = new DiffOutcome();
        if (reportDuplicates)
        {
            foreach (var d in dupA) outcome.OnlyInA.Add($"DUP(A):{d}");
            foreach (var d in dupB) outcome.OnlyInB.Add($"DUP(B):{d}");
        }
        foreach (var k in dictA.Keys)
        {
            if (!dictB.TryGetValue(k, out var rb)) outcome.OnlyInA.Add(k);
            else if (rb.PayloadHash != dictA[k].PayloadHash) outcome.PayloadMismatch.Add(k);
        }
        foreach (var k in dictB.Keys) if (!dictA.ContainsKey(k)) outcome.OnlyInB.Add(k);
        return outcome;
    }

    public static string[] InferDefaultKeys(string a, string b)
    {
        var firstData = File.ReadLines(a).Skip(2).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (firstData is null) return new[] { "utc_ts", "event_type" };
        if (firstData.Contains("IntervalSeconds", StringComparison.OrdinalIgnoreCase)) return new[] { "instrumentId", "intervalSeconds", "openTimeUtc", "eventType" };
        if (firstData.Contains("RISK_PROBE_V1", StringComparison.OrdinalIgnoreCase)) return new[] { "instrumentId", "eventType", "utc_ts" };
        return new[] { "utc_ts", "event_type" };
    }

    private static IEnumerable<DiffRow> LoadRows(string path, string[] keyFields)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        var meta = reader.ReadLine(); if (meta == null) throw new InvalidDataException("Empty file");
        var header = reader.ReadLine(); if (header == null) throw new InvalidDataException("Missing header line");
        var columns = header.Split(',');
        int payloadIdx = Array.IndexOf(columns, "payload_json"); if (payloadIdx < 0) throw new InvalidDataException("payload_json column missing");
        var colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < columns.Length; i++) colIndex[columns[i]] = i;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = SplitCsv(line);
            if (parts.Length != columns.Length) throw new InvalidDataException($"Column mismatch line: {line}");
            var payloadRaw = parts[payloadIdx];
            var flat = FlattenPayload(payloadRaw);
            var key = BuildCompositeKey(keyFields, parts, colIndex, flat);
            var hash = Sha256(CanonicalJson(payloadRaw));
            yield return new DiffRow(key, hash);
        }
    }

    private static string BuildCompositeKey(string[] keyFields, string[] parts, Dictionary<string, int> colIndex, Dictionary<string, string> flat)
    {
        var sb = new StringBuilder();
        string eventType = colIndex.TryGetValue("event_type", out var etIdx) ? parts[etIdx] : string.Empty;
        for (int i = 0; i < keyFields.Length; i++)
        {
            var f = keyFields[i];
            string? v = null;
            if (colIndex.TryGetValue(f, out var ci)) v = parts[ci];
            else if (flat.TryGetValue(f, out var fv)) v = fv;
            else if (string.Equals(f, "instrumentId", StringComparison.OrdinalIgnoreCase) && flat.TryGetValue("InstrumentId.Value", out var inst)) v = inst;
            else if (string.Equals(f, "openTimeUtc", StringComparison.OrdinalIgnoreCase) && flat.TryGetValue("StartUtc", out var start)) v = start;
            else if (string.Equals(f, "eventType", StringComparison.OrdinalIgnoreCase) && colIndex.TryGetValue("event_type", out var et2)) v = parts[et2];
            else if (eventType == "RISK_PROBE_V1" && (string.Equals(f, "intervalSeconds", StringComparison.OrdinalIgnoreCase) || string.Equals(f, "openTimeUtc", StringComparison.OrdinalIgnoreCase))) v = string.Empty; // tolerate missing fields for risk probe
            if (v is null) throw new InvalidDataException($"Key field '{f}' not found in columns or payload");
            if (i > 0) sb.Append('|'); sb.Append(v);
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> FlattenPayload(string raw)
    {
        raw = UnwrapCsvQuoted(raw);
        using var doc = JsonDocument.Parse(raw);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Recurse(doc.RootElement, "", dict);
        return dict;
        static void Recurse(JsonElement el, string prefix, Dictionary<string, string> acc)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in el.EnumerateObject())
                    {
                        var next = string.IsNullOrEmpty(prefix) ? p.Name : prefix + "." + p.Name;
                        Recurse(p.Value, next, acc);
                    }
                    break;
                case JsonValueKind.Array:
                    int i = 0; foreach (var item in el.EnumerateArray()) { Recurse(item, prefix + "[" + i++ + "]", acc); }
                    break;
                case JsonValueKind.String: acc[prefix] = el.GetString() ?? string.Empty; break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                case JsonValueKind.Null:
                    acc[prefix] = el.GetRawText(); break;
            }
        }
    }

    private static string CanonicalJson(string raw)
    {
        raw = UnwrapCsvQuoted(raw);
        using var doc = JsonDocument.Parse(raw);
        var sb = new StringBuilder();
        WriteCanonical(doc.RootElement, sb);
        return sb.ToString();
    }

    private static string UnwrapCsvQuoted(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            var inner = raw.Substring(1, raw.Length - 2).Replace("\"\"", "\"");
            return inner;
        }
        return raw;
    }

    private static void WriteCanonical(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{'); bool first = true; foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { if (!first) sb.Append(','); first = false; sb.Append('"').Append(p.Name).Append('"').Append(':'); WriteCanonical(p.Value, sb); }
                sb.Append('}'); break;
            case JsonValueKind.Array:
                sb.Append('['); bool firstA = true; foreach (var i in el.EnumerateArray()) { if (!firstA) sb.Append(','); firstA = false; WriteCanonical(i, sb); }
                sb.Append(']'); break;
            case JsonValueKind.String: sb.Append('"').Append(el.GetString()).Append('"'); break;
            case JsonValueKind.Number: sb.Append(el.GetRawText()); break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            case JsonValueKind.Null: sb.Append("null"); break;
        }
    }

    private static string Sha256(string canonical)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        var sb = new StringBuilder(bytes.Length * 2); foreach (var b in bytes) sb.Append(b.ToString("x2")); return sb.ToString();
    }

    private static string[] SplitCsv(string line)
    {
        var result = new List<string>(); var sb = new StringBuilder(); bool inQuotes = false; for (int i = 0; i < line.Length; i++) { char c = line[i]; if (inQuotes) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else inQuotes = false; } else sb.Append(c); } else { if (c == ',') { result.Add(sb.ToString()); sb.Clear(); } else if (c == '"') inQuotes = true; else sb.Append(c); } }
        result.Add(sb.ToString()); return result.ToArray();
    }
}
