using System.Text;
using System.Text.Json;

// Clean diff CLI implementation (reset after previous merge corruption)
var argv = args.ToList();
if (argv.Count == 0 || argv[0].Equals("--help", StringComparison.OrdinalIgnoreCase)) { PrintHelp(); return 2; }
if (!string.Equals(argv[0], "diff", StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("Only 'diff' command is supported."); PrintHelp(); return 2; }

string? fileA = null, fileB = null, keyList = null; bool reportDup = false;
for (int i=1;i<argv.Count;i++)
{
    switch (argv[i])
    {
        case "--a": fileA = (++i<argv.Count)?argv[i]:null; break;
        case "--b": fileB = (++i<argv.Count)?argv[i]:null; break;
        case "--keys": keyList = (++i<argv.Count)?argv[i]:null; break;
        case "--report-duplicates": reportDup = true; break;
        default: Console.Error.WriteLine($"Unknown option {argv[i]}"); return 2;
    }
}
if (string.IsNullOrWhiteSpace(fileA) || string.IsNullOrWhiteSpace(fileB)) { Console.Error.WriteLine("--a and --b are required"); return 2; }

try
{
    var keys = !string.IsNullOrWhiteSpace(keyList) ? keyList.Split(',', StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries) : DiffEngine.InferDefaultKeys(fileA!, fileB!);
    var outcome = DiffEngine.Run(fileA!, fileB!, keys, reportDup);
    if (!outcome.HasDiff) { Console.WriteLine("No differences detected."); return 0; }
    Console.WriteLine(outcome.GetSummary(50));
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 2;
}

static void PrintHelp() => Console.WriteLine("Usage: diff --a <fileA> --b <fileB> [--keys k1,k2,...] [--report-duplicates]");

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
