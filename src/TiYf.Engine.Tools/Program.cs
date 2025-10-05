using System.Text;
using System.Text.Json;

// Clean diff/verify CLI implementation (reset after previous merge corruption)
var argv = args.ToList();
if (argv.Count == 0 || argv[0].Equals("--help", StringComparison.OrdinalIgnoreCase)) { PrintHelp(); return 2; }
var command = argv[0].ToLowerInvariant();
try
{
    return command switch
    {
        "diff" => RunDiff(argv.Skip(1).ToList()),
        "verify" => RunVerify(argv.Skip(1).ToList()),
        _ => Unknown()
    };
}
catch (VerifyFatalException vf)
{
    Console.Error.WriteLine($"FATAL: {vf.Message}");
    return 2; // fatal error
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

Exit codes verify: 0 OK, 1 validation issues, 2 fatal (IO/format)");

static int RunDiff(List<string> args)
{
    string? fileA = null, fileB = null, keyList = null; bool reportDup = false;
    for (int i=0;i<args.Count;i++)
    {
        switch (args[i])
        {
            case "--a": fileA = (++i<args.Count)?args[i]:null; break;
            case "--b": fileB = (++i<args.Count)?args[i]:null; break;
            case "--keys": keyList = (++i<args.Count)?args[i]:null; break;
            case "--report-duplicates": reportDup = true; break;
            default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 2;
        }
    }
    if (string.IsNullOrWhiteSpace(fileA) || string.IsNullOrWhiteSpace(fileB)) { Console.Error.WriteLine("--a and --b are required"); return 2; }
    var keys = !string.IsNullOrWhiteSpace(keyList) ? keyList.Split(',', StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries) : DiffEngine.InferDefaultKeys(fileA!, fileB!);
    var outcome = DiffEngine.Run(fileA!, fileB!, keys, reportDup);
    if (!outcome.HasDiff) { Console.WriteLine("No differences detected."); return 0; }
    Console.WriteLine(outcome.GetSummary(50));
    return 1;
}

static int RunVerify(List<string> args)
{
    string? file = null; bool json=false; int maxErrors=50; bool reportDup=false;
    for (int i=0;i<args.Count;i++)
    {
        switch (args[i])
        {
            case "--file": file = (++i<args.Count)?args[i]:null; break;
            case "--json": json = true; break;
            case "--max-errors": if(++i<args.Count && int.TryParse(args[i], out var m)) maxErrors = m; else throw new VerifyFatalException("--max-errors requires integer"); break;
            case "--report-duplicates": reportDup = true; break;
            default: throw new VerifyFatalException($"Unknown option {args[i]}");
        }
    }
    if (string.IsNullOrWhiteSpace(file)) throw new VerifyFatalException("--file required");
    var result = VerifyEngine.Run(file!, new VerifyOptions(maxErrors, json, reportDup));
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

// ===== VERIFY IMPLEMENTATION =====
public sealed record VerifyOptions(int MaxErrors, bool Json, bool ReportDuplicates);
public sealed class VerifyResult
{
    public int ExitCode { get; init; }
    public string HumanSummary { get; init; } = string.Empty;
    public string? JsonOutput { get; init; }
}
public sealed class VerifyFatalException : Exception { public VerifyFatalException(string msg):base(msg){} }

public static class VerifyEngine
{
    public static VerifyResult Run(string path, VerifyOptions options)
    {
        if (!File.Exists(path)) throw new VerifyFatalException($"File not found: {path}");
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        string? meta = reader.ReadLine();
        string? header = reader.ReadLine();
        if (meta == null || header == null) throw new VerifyFatalException("Missing meta/header lines");
        var metaParts = meta.Split(',', StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        string? schemaVersion = null; string? configHash = null;
        foreach (var p in metaParts)
        {
            var kv = p.Split('='); if (kv.Length==2){ if(kv[0]=="schema_version") schemaVersion=kv[1]; else if(kv[0]=="config_hash") configHash=kv[1]; }
        }
        var errors = new List<(string Key,string Reason)>();
        void AddErr(string key,string reason){ if(errors.Count < options.MaxErrors) errors.Add((key,reason)); }
        if (string.IsNullOrWhiteSpace(schemaVersion)) AddErr("<meta>","schema_version missing");
    else if (schemaVersion != TiYf.Engine.Core.Infrastructure.Schema.Version) AddErr("<meta>", $"schema_version mismatch expected {TiYf.Engine.Core.Infrastructure.Schema.Version} got {schemaVersion}");
        if (string.IsNullOrWhiteSpace(configHash)) AddErr("<meta>","config_hash missing");

        var columns = header.Split(',');
        int seqIdx = Array.IndexOf(columns,"sequence");
        int tsIdx = Array.IndexOf(columns,"utc_ts");
        int etIdx = Array.IndexOf(columns,"event_type");
        int payloadIdx = Array.IndexOf(columns,"payload_json");
        if (seqIdx<0||tsIdx<0||etIdx<0||payloadIdx<0) throw new VerifyFatalException("Required columns missing in header");

        var seenKeys = new HashSet<string>();
        var lastOpenTimes = new Dictionary<string, DateTime>(); // key: instrument|interval
    // tracking variable removed (was unused in exit code decision)
        string? line; long lineNum=2; // already consumed 2 lines
        while ((line = reader.ReadLine()) != null)
        {
            lineNum++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] parts;
            try { parts = SplitCsv(line); }
            catch(Exception ex){ throw new VerifyFatalException($"CSV parse error line {lineNum}: {ex.Message}"); }
            if (parts.Length != columns.Length){ throw new VerifyFatalException($"Column count mismatch line {lineNum}"); }
            var evtType = parts[etIdx];
            var tsRaw = parts[tsIdx];
            if (!TryParseUtc(tsRaw, out var rowTs)) { AddErr($"line:{lineNum}","utc_ts not valid ISO-8601 UTC"); }
            var payloadRaw = parts[payloadIdx];
            var payloadText = UnwrapCsvQuoted(payloadRaw);
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(payloadText); }
            catch { AddErr($"line:{lineNum}","payload_json not valid JSON"); continue; }
            var root = doc.RootElement;
            if (evtType == "BAR_V1")
            {
                // Required fields
                string? inst = TryGet(root, "InstrumentId.Value");
                string? intervalSecStr = TryGet(root, "IntervalSeconds");
                string? startUtcStr = TryGet(root, "StartUtc");
                string? endUtcStr = TryGet(root, "EndUtc");
                string? openStr = TryGet(root, "Open");
                string? highStr = TryGet(root, "High");
                string? lowStr = TryGet(root, "Low");
                string? closeStr = TryGet(root, "Close");
                string? volStr = TryGet(root, "Volume");
                if (inst is null|| intervalSecStr is null|| startUtcStr is null|| endUtcStr is null|| openStr is null|| highStr is null|| lowStr is null|| closeStr is null|| volStr is null)
                { AddErr($"BAR:{inst ?? "?"}:{startUtcStr ?? "?"}","missing required bar fields"); }
                else
                {
                    if (!int.TryParse(intervalSecStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var intervalSeconds) || intervalSeconds <= 0)
                    { AddErr($"BAR:{inst}:{startUtcStr}","invalid intervalSeconds"); }
                    if (!TryParseUtc(startUtcStr, out var startUtc) || !TryParseUtc(endUtcStr, out var endUtc))
                    { AddErr($"BAR:{inst}:{startUtcStr}","start/end not valid UTC"); }
                    else
                    {
                        if (!(startUtc < endUtc)) { AddErr($"BAR:{inst}:{startUtcStr}","startUtc >= endUtc"); }
                        var key = $"{inst}|{intervalSeconds}|{startUtc:O}|BAR_V1";
                        if (!seenKeys.Add(key)) { if(options.ReportDuplicates) AddErr(key,"duplicate composite key"); }
                        var monotonicKey = $"{inst}|{intervalSeconds}";
                        if (lastOpenTimes.TryGetValue(monotonicKey, out var prev) && !(startUtc > prev)) { AddErr(key,"non-monotonic startUtc"); }
                        lastOpenTimes[monotonicKey] = startUtc;
                    }
                }
            }
            else if (evtType == "RISK_PROBE_V1")
            {
                string? inst = TryGet(root, "InstrumentId.Value");
                string? leverage = TryGet(root, "ProjectedLeverage");
                string? margin = TryGet(root, "ProjectedMarginUsagePct");
                string? basket = TryGet(root, "BasketRiskPct");
                if (inst is null || leverage is null || margin is null || basket is null)
                { AddErr($"RISK:{inst ?? "?"}:{tsRaw}","missing required risk fields"); }
                else
                {
                    if (!double.TryParse(leverage, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) { AddErr($"RISK:{inst}:{tsRaw}","ProjectedLeverage not number"); }
                    if (!double.TryParse(margin, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) { AddErr($"RISK:{inst}:{tsRaw}","ProjectedMarginUsagePct not number"); }
                    if (!double.TryParse(basket, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) { AddErr($"RISK:{inst}:{tsRaw}","BasketRiskPct not number"); }
                }
            }
            else
            {
                // Unknown event types tolerated but flagged
                AddErr($"line:{lineNum}", $"unsupported event_type {evtType}"); }
        }

        int exitCode = errors.Count == 0 ? 0 : 1;
        var human = new StringBuilder();
        if (exitCode==0) human.AppendLine("OK: journal verification passed");
        else
        {
            human.AppendLine($"VERIFICATION FAILED: {errors.Count} issue(s) (showing up to {options.MaxErrors})");
            foreach (var e in errors) human.AppendLine($" - {e.Key}: {e.Reason}");
        }
        string? jsonOut = null;
        if (options.Json)
        {
            var obj = new
            {
                ok = exitCode==0,
                errorCount = errors.Count,
                errors = errors.Select(e => new { key = e.Key, reason = e.Reason }).ToArray()
            };
            jsonOut = System.Text.Json.JsonSerializer.Serialize(obj, new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
        }
        return new VerifyResult{ ExitCode = exitCode, HumanSummary = human.ToString().TrimEnd(), JsonOutput = jsonOut };
    }

    private static bool TryParseUtc(string value, out DateTime dt)
    {
        if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out dt))
            return dt.Kind == DateTimeKind.Utc;
        return false;
    }

    private static string? TryGet(JsonElement root, string dotted)
    {
        var parts = dotted.Split('.');
        JsonElement current = root;
        for (int i=0;i<parts.Length;i++)
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            var found = false;
            foreach (var p in current.EnumerateObject())
            {
                if (string.Equals(p.Name, parts[i], StringComparison.OrdinalIgnoreCase)) { current = p.Value; found = true; break; }
            }
            if (!found) return null;
        }
        if (current.ValueKind == JsonValueKind.String) return current.GetString();
        if (current.ValueKind == JsonValueKind.Number) return current.GetRawText();
        if (current.ValueKind == JsonValueKind.True || current.ValueKind == JsonValueKind.False || current.ValueKind == JsonValueKind.Null) return current.GetRawText();
        return current.GetRawText();
    }

    // Local minimal copies (avoid dependency on diff section ordering)
    private static string UnwrapCsvQuoted(string raw)
    {
        if (raw.Length >=2 && raw[0]=='"' && raw[^1]=='"')
        {
            var inner = raw.Substring(1, raw.Length-2);
            // CSV escaping doubles quotes inside a quoted field
            return inner.Replace("\"\"", "\"");
        }
        return raw;
    }
    private static string[] SplitCsv(string line)
    {
        var result = new List<string>(); var sb = new StringBuilder(); bool inQuotes=false;
        for (int i=0;i<line.Length;i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c=='"')
                {
                    if (i+1<line.Length && line[i+1]=='"') { sb.Append('"'); i++; }
                    else inQuotes=false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c==','){ result.Add(sb.ToString()); sb.Clear(); }
                else if (c=='"') inQuotes=true;
                else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result.ToArray();
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
        // Inspect first non-header data line of file A
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
            else if (eventType == "RISK_PROBE_V1" && (string.Equals(f,"intervalSeconds",StringComparison.OrdinalIgnoreCase) || string.Equals(f,"openTimeUtc",StringComparison.OrdinalIgnoreCase))) v = string.Empty; // tolerate missing for risk probe
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
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            // Remove surrounding quotes and unescape doubled quotes per RFC 4180.
            var inner = raw.Substring(1, raw.Length - 2).Replace("\"\"", "\""); // Replace doubled quotes "" with "
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
