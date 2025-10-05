using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TiYf.Engine.Tools; // VerifyEngine types

// ------------------------------------------------------------
// CLI entrypoint: supports 'diff' and 'verify'
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
    return 2;
}

static int Unknown(){ Console.Error.WriteLine("Unknown command"); PrintHelp(); return 2; }
static void PrintHelp() => Console.WriteLine(@"Usage:
  diff   --a <fileA> --b <fileB> [--keys k1,k2,...] [--report-duplicates]
  verify --file <journal.csv> [--json] [--max-errors N] [--report-duplicates]
  dataversion --config <config.json> [--instruments path] [--ticks SYMBOL=path ...] [--out data_version.txt] [--echo-rows]");

static int RunDataVersion(List<string> args)
{
    string? config=null; string? instrumentsOverride=null; var tickOverrides = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); string? outFile=null; bool echoRows=false;
    for (int i=0;i<args.Count;i++)
    {
        switch(args[i])
        {
            case "--config": config = (++i<args.Count)? args[i]:null; break;
            case "--instruments": instrumentsOverride = (++i<args.Count)? args[i]:null; break;
            case "--ticks":
                if (++i<args.Count)
                {
                    var spec = args[i];
                    var kv = spec.Split('='); if (kv.Length==2) tickOverrides[kv[0]] = kv[1];
                }
                break;
            case "--out": outFile = (++i<args.Count)? args[i]:null; break;
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
        instruments = root.TryGetProperty("data", out var dataEl) && dataEl.TryGetProperty("instrumentsFile", out var instEl) && instEl.ValueKind==JsonValueKind.String ? instEl.GetString() : null;
    }
    if (instruments == null || !File.Exists(instruments)) { Console.Error.WriteLine("Cannot resolve instruments file"); return 2; }
    var tickMap = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    if (root.TryGetProperty("data", out var dataEl2) && dataEl2.TryGetProperty("ticks", out var ticksEl) && ticksEl.ValueKind==JsonValueKind.Object)
    {
        foreach (var p in ticksEl.EnumerateObject()) if (p.Value.ValueKind==JsonValueKind.String) tickMap[p.Name] = p.Value.GetString()!;
    }
    // Apply overrides
    foreach (var kv in tickOverrides) tickMap[kv.Key] = kv.Value;
    // Required symbols for ordering: EURUSD, USDJPY, XAUUSD (if present in config)
    string[] ordering = new[]{"EURUSD","USDJPY","XAUUSD"};
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

static int CountDataLines(string path) => Math.Max(0, File.ReadLines(path).Count()-1);

static int RunDiff(List<string> args)
{
    string? fileA=null,fileB=null,keyList=null; bool reportDup=false;
    for (int i=0;i<args.Count;i++)
    {
        switch(args[i])
        {
            case "--a": fileA=(++i<args.Count)?args[i]:null; break;
            case "--b": fileB=(++i<args.Count)?args[i]:null; break;
            case "--keys": keyList=(++i<args.Count)?args[i]:null; break;
            case "--report-duplicates": reportDup=true; break;
            default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 2;
        }
    }
    if (string.IsNullOrWhiteSpace(fileA) || string.IsNullOrWhiteSpace(fileB)) { Console.Error.WriteLine("--a and --b required"); return 2; }
    var keys = !string.IsNullOrWhiteSpace(keyList)? keyList.Split(',', StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries) : DiffEngine.InferDefaultKeys(fileA!, fileB!);
    var outcome = DiffEngine.Run(fileA!, fileB!, keys, reportDup);
    if (!outcome.HasDiff) { Console.WriteLine("No differences detected."); return 0; }
    Console.WriteLine(outcome.GetSummary(50));
    return 1;
}

static int RunVerify(List<string> args)
{
    string? file=null; bool json=false; int maxErrors=50; bool reportDup=false;
    for (int i=0;i<args.Count;i++)
    {
        switch(args[i])
        {
            case "--file": file=(++i<args.Count)?args[i]:null; break;
            case "--json": json=true; break;
            case "--max-errors": if(++i<args.Count && int.TryParse(args[i], out var m)) maxErrors=m; else throw new VerifyFatalException("--max-errors requires integer"); break;
            case "--report-duplicates": reportDup=true; break;
            default: throw new VerifyFatalException($"Unknown option {args[i]}");
        }
    }
    if (string.IsNullOrWhiteSpace(file)) throw new VerifyFatalException("--file required");
    var result = VerifyEngine.Run(file!, new VerifyOptions(maxErrors,json,reportDup));
    if (result.JsonOutput != null) Console.WriteLine(result.JsonOutput); else Console.WriteLine(result.HumanSummary);
    return result.ExitCode;
}

internal record DiffRow(string CompositeKey, string PayloadHash);
public sealed class DiffOutcome
{
    public List<string> OnlyInA { get; } = new();
    public List<string> OnlyInB { get; } = new();
    public List<string> PayloadMismatch { get; } = new();
    public bool HasDiff => OnlyInA.Count>0 || OnlyInB.Count>0 || PayloadMismatch.Count>0;
    public string GetSummary(int limit)
    {
        if (!HasDiff) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine("DIFF SUMMARY:");
        if (OnlyInA.Count>0) sb.AppendLine($"  Present only in A: {string.Join(';', OnlyInA.Take(limit))}");
        if (OnlyInB.Count>0) sb.AppendLine($"  Present only in B: {string.Join(';', OnlyInB.Take(limit))}");
        if (PayloadMismatch.Count>0) sb.AppendLine($"  Payload mismatches: {string.Join(';', PayloadMismatch.Take(limit))}");
        sb.AppendLine($"(showing at most {limit} keys per category)");
        return sb.ToString();
    }
}

public static class DiffEngine
{
    public static DiffOutcome Run(string pathA, string pathB, string[] keyFields, bool reportDuplicates=false)
    {
        var rowsA = LoadRows(pathA, keyFields);
        var rowsB = LoadRows(pathB, keyFields);
        var dictA = new Dictionary<string,DiffRow>(); var dupA = new HashSet<string>();
        foreach (var r in rowsA) if (!dictA.ContainsKey(r.CompositeKey)) dictA[r.CompositeKey]=r; else dupA.Add(r.CompositeKey);
        var dictB = new Dictionary<string,DiffRow>(); var dupB = new HashSet<string>();
        foreach (var r in rowsB) if (!dictB.ContainsKey(r.CompositeKey)) dictB[r.CompositeKey]=r; else dupB.Add(r.CompositeKey);
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
        var firstData = File.ReadLines(a).Skip(2).FirstOrDefault(l=>!string.IsNullOrWhiteSpace(l));
        if (firstData is null) return new[]{"utc_ts","event_type"};
        if (firstData.Contains("IntervalSeconds", StringComparison.OrdinalIgnoreCase)) return new[]{"instrumentId","intervalSeconds","openTimeUtc","eventType"};
        if (firstData.Contains("RISK_PROBE_V1", StringComparison.OrdinalIgnoreCase)) return new[]{"instrumentId","eventType","utc_ts"};
        return new[]{"utc_ts","event_type"};
    }

    private static IEnumerable<DiffRow> LoadRows(string path, string[] keyFields)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        var meta = reader.ReadLine(); if (meta == null) throw new InvalidDataException("Empty file");
        var header = reader.ReadLine(); if (header == null) throw new InvalidDataException("Missing header line");
        var columns = header.Split(',');
        int payloadIdx = Array.IndexOf(columns, "payload_json"); if (payloadIdx < 0) throw new InvalidDataException("payload_json column missing");
        var colIndex = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        for (int i=0;i<columns.Length;i++) colIndex[columns[i]] = i;
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

    private static string BuildCompositeKey(string[] keyFields, string[] parts, Dictionary<string,int> colIndex, Dictionary<string,string> flat)
    {
        var sb = new StringBuilder();
        string eventType = colIndex.TryGetValue("event_type", out var etIdx) ? parts[etIdx] : string.Empty;
        for (int i=0;i<keyFields.Length;i++)
        {
            var f = keyFields[i];
            string? v = null;
            if (colIndex.TryGetValue(f, out var ci)) v = parts[ci];
            else if (flat.TryGetValue(f, out var fv)) v = fv;
            else if (string.Equals(f,"instrumentId",StringComparison.OrdinalIgnoreCase) && flat.TryGetValue("InstrumentId.Value", out var inst)) v = inst;
            else if (string.Equals(f,"openTimeUtc",StringComparison.OrdinalIgnoreCase) && flat.TryGetValue("StartUtc", out var start)) v = start;
            else if (string.Equals(f,"eventType",StringComparison.OrdinalIgnoreCase) && colIndex.TryGetValue("event_type", out var et2)) v = parts[et2];
            else if (eventType == "RISK_PROBE_V1" && (string.Equals(f,"intervalSeconds",StringComparison.OrdinalIgnoreCase) || string.Equals(f,"openTimeUtc",StringComparison.OrdinalIgnoreCase))) v = string.Empty; // tolerate missing fields for risk probe
            if (v is null) throw new InvalidDataException($"Key field '{f}' not found in columns or payload");
            if (i>0) sb.Append('|'); sb.Append(v);
        }
        return sb.ToString();
    }

    private static Dictionary<string,string> FlattenPayload(string raw)
    {
        raw = UnwrapCsvQuoted(raw);
        using var doc = JsonDocument.Parse(raw);
        var dict = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        Recurse(doc.RootElement, "", dict);
        return dict;
        static void Recurse(JsonElement el, string prefix, Dictionary<string,string> acc)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in el.EnumerateObject())
                    {
                        var next = string.IsNullOrEmpty(prefix)? p.Name : prefix+"."+p.Name;
                        Recurse(p.Value, next, acc);
                    }
                    break;
                case JsonValueKind.Array:
                    int i=0; foreach (var item in el.EnumerateArray()) { Recurse(item, prefix+"["+i+++"]", acc);} break;
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
        if (raw.Length >= 2 && raw[0]=='"' && raw[^1]=='"')
        {
            var inner = raw.Substring(1, raw.Length-2).Replace("\"\"","\"");
            return inner;
        }
        return raw;
    }

    private static void WriteCanonical(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{'); bool first=true; foreach (var p in el.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal)) { if(!first) sb.Append(','); first=false; sb.Append('"').Append(p.Name).Append('"').Append(':'); WriteCanonical(p.Value,sb);} sb.Append('}'); break;
            case JsonValueKind.Array:
                sb.Append('['); bool firstA=true; foreach (var i in el.EnumerateArray()) { if(!firstA) sb.Append(','); firstA=false; WriteCanonical(i,sb);} sb.Append(']'); break;
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
        var sb = new StringBuilder(bytes.Length*2); foreach (var b in bytes) sb.Append(b.ToString("x2")); return sb.ToString();
    }

    private static string[] SplitCsv(string line)
    {
        var result = new List<string>(); var sb = new StringBuilder(); bool inQuotes=false; for(int i=0;i<line.Length;i++){ char c=line[i]; if(inQuotes){ if(c=='"'){ if(i+1<line.Length && line[i+1]=='"'){ sb.Append('"'); i++; } else inQuotes=false; } else sb.Append(c);} else { if(c==','){ result.Add(sb.ToString()); sb.Clear(); } else if(c=='"') inQuotes=true; else sb.Append(c);} } result.Add(sb.ToString()); return result.ToArray();
    }
}
