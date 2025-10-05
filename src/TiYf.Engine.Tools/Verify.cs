using System.Text;
using System.Text.Json;
using TiYf.Engine.Core.Infrastructure;

namespace TiYf.Engine.Tools;

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
        else if (schemaVersion != Schema.Version) AddErr("<meta>", $"schema_version mismatch expected {Schema.Version} got {schemaVersion}");
        if (string.IsNullOrWhiteSpace(configHash)) AddErr("<meta>","config_hash missing");

                var columns = header.Split(',');
                int seqIdx = Array.IndexOf(columns,"sequence");
                int tsIdx = Array.IndexOf(columns,"utc_ts");
                int etIdx = Array.IndexOf(columns,"event_type");
                int payloadIdx = Array.IndexOf(columns,"payload_json");
                if (seqIdx<0||tsIdx<0||etIdx<0||payloadIdx<0) throw new VerifyFatalException("Required columns missing in header");

                var seenKeys = new HashSet<string>();
                var lastOpenTimes = new Dictionary<string, DateTime>();
                string? line; long lineNum=2;
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
                    if (!TryParseUtc(tsRaw, out _)) { AddErr($"line:{lineNum}","utc_ts not valid ISO-8601 UTC"); }
                    var payloadRaw = parts[payloadIdx];
                    var payloadText = UnwrapCsvQuoted(payloadRaw);
                    JsonDocument? doc = null;
                    try { doc = JsonDocument.Parse(payloadText); }
                    catch { AddErr($"line:{lineNum}","payload_json not valid JSON"); continue; }
                    var root = doc.RootElement;
                    if (evtType == "BAR_V1")
                    {
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
                                // Accept either nested object (InstrumentId.Value) or flat string InstrumentId
                                string? inst = TryGet(root, "InstrumentId.Value") ?? TryGet(root, "InstrumentId");
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
                        // Whitelist alert / scaling informational events (light validation only)
                        if (evtType is "ALERT_BLOCK_LEVERAGE" or "ALERT_BLOCK_MARGIN" or "ALERT_BLOCK_RISK_CAP" or "ALERT_BLOCK_BASKET" or "INFO_SCALE_TO_FIT")
                        {
                            // Ensure basic expected fields exist (DecisionId & InstrumentId) but do not enforce numeric semantics yet
                            string? inst = TryGet(root, "InstrumentId") ?? TryGet(root, "InstrumentId.Value");
                            string? decision = TryGet(root, "DecisionId");
                            if (string.IsNullOrWhiteSpace(inst) || string.IsNullOrWhiteSpace(decision))
                            {
                                AddErr($"{evtType}:{tsRaw}", "missing InstrumentId or DecisionId");
                            }
                        }
                        else
                        {
                            AddErr($"line:{lineNum}", $"unsupported event_type {evtType}");
                        }
                    }
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
                    jsonOut = JsonSerializer.Serialize(obj, new JsonSerializerOptions{WriteIndented=true});
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
            private static string UnwrapCsvQuoted(string raw)
            {
                if (raw.Length >=2 && raw[0]=='"' && raw[^1]=='"')
                {
                    var inner = raw.Substring(1, raw.Length-2);
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