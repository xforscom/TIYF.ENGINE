using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TiYf.Engine.Tools;

public sealed record StrictVerifyRequest(string EventsPath, string TradesPath, string MinimumSchema, bool strict, string? sentimentMode = null);
public sealed record StrictViolation(string Kind, ulong Sequence, string? Symbol, string? Ts, string Detail);
public sealed record StrictVerifyReport(int ExitCode, List<StrictViolation> Violations, string JsonReport);

public static class StrictJournalVerifier
{
    private static readonly HashSet<string> KnownEventTypes = new(StringComparer.Ordinal)
    {
        "BAR_V1","RISK_PROBE_V1",
        "INFO_SENTIMENT_Z_V1","INFO_SENTIMENT_CLAMP_V1","INFO_SENTIMENT_APPLIED_V1",
        "DATA_QA_BEGIN_V1","DATA_QA_ISSUE_V1","DATA_QA_SUMMARY_V1","DATA_QA_ABORT_V1"
    };

    private sealed record ParsedEvent(ulong Seq, DateTime Ts, string Type, JsonElement Payload);

    public static StrictVerifyReport Verify(StrictVerifyRequest req)
    {
        var violations = new List<StrictViolation>();
        void V(string kind, ulong seq, string? sym, string? ts, string detail){ violations.Add(new StrictViolation(kind, seq, sym, ts, detail)); }

        if (!File.Exists(req.EventsPath)) throw new FileNotFoundException(req.EventsPath);
        if (!File.Exists(req.TradesPath)) throw new FileNotFoundException(req.TradesPath);

        // Parse events
        var lines = File.ReadAllLines(req.EventsPath).Where(l=>!string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count < 2) V("file_format",0,null,null,"events missing meta/header");
        string schema = "";
        if (lines.Count >= 1)
        {
            var metaParts = lines[0].Split(',', StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
            foreach (var p in metaParts)
            {
                var kv = p.Split('='); if (kv.Length==2 && kv[0]=="schema_version") schema = kv[1];
            }
        }
        if (string.IsNullOrWhiteSpace(schema) || string.Compare(schema, req.MinimumSchema, StringComparison.Ordinal) < 0)
            V("schema_version",0,null,null,$"schema_version {schema} < required {req.MinimumSchema}");

        var parsed = new List<ParsedEvent>();
        ulong lastSeq = 0; DateTime lastTs = DateTime.MinValue;
        for (int i=2;i<lines.Count;i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] cols = SplitCsv(line);
            if (cols.Length < 4) { V("row_format",0,null,null,$"line {i+1} malformed"); continue; }
            if (!ulong.TryParse(cols[0], out var seq)) { V("sequence_parse",0,null,null,$"line {i+1} sequence invalid"); continue; }
            var tsRaw = cols[1]; var evtType = cols[2]; var payloadRaw = UnwrapCsvQuoted(cols[3]);
            if (seq <= lastSeq) V("order_violation", seq, null, tsRaw, "non-increasing sequence"); else lastSeq = seq;
            if (!DateTime.TryParse(tsRaw, null, DateTimeStyles.RoundtripKind, out var ts) || ts.Kind!=DateTimeKind.Utc) V("timestamp", seq,null, tsRaw, "invalid or non-UTC timestamp");
            else if (ts < lastTs) V("order_violation", seq,null, tsRaw, "timestamp regression"); else lastTs = ts;
            if (!KnownEventTypes.Contains(evtType)) { V("unknown_event", seq,null, tsRaw, evtType); continue; }
            JsonElement payload;
            try { using var doc = JsonDocument.Parse(payloadRaw); payload = doc.RootElement.Clone(); }
            catch { V("payload_json", seq,null, tsRaw, "invalid JSON"); continue; }
            parsed.Add(new ParsedEvent(seq, ts, evtType, payload));
        }

        // Ordering rules relative placement within bar (approx by looking for sequences): enforce pattern BAR -> Z -> CLAMP? -> APPLIED?
        var barSeqs = parsed.Where(p=>p.Type=="BAR_V1").Select(p=>p.Seq).ToHashSet();
        foreach (var ev in parsed)
        {
            if (ev.Type=="INFO_SENTIMENT_Z_V1")
            {
                // previous must be BAR_V1 (not strictly immediate if data QA events existed earlier, but for scaffold strictness keep simple)
                var prev = parsed.FirstOrDefault(p=>p.Seq == ev.Seq-1);
                if (prev==null || prev.Type!="BAR_V1") V("order_violation", ev.Seq, GetSym(ev.Payload), ev.Ts.ToString("O"), "Z must immediately follow BAR");
            }
            else if (ev.Type=="INFO_SENTIMENT_CLAMP_V1")
            {
                var prev = parsed.FirstOrDefault(p=>p.Seq == ev.Seq-1);
                if (prev==null || prev.Type!="INFO_SENTIMENT_Z_V1") V("order_violation", ev.Seq, GetSym(ev.Payload), ev.Ts.ToString("O"), "CLAMP must follow Z");
            }
            else if (ev.Type=="INFO_SENTIMENT_APPLIED_V1")
            {
                var prev = parsed.FirstOrDefault(p=>p.Seq == ev.Seq-1);
                if (prev==null || (prev.Type!="INFO_SENTIMENT_Z_V1" && prev.Type!="INFO_SENTIMENT_CLAMP_V1")) V("order_violation", ev.Seq, GetSym(ev.Payload), ev.Ts.ToString("O"), "APPLIED must follow Z or CLAMP");
            }
        }

        // Field requirements for INFO_SENTIMENT_APPLIED_V1
        foreach (var ap in parsed.Where(p=>p.Type=="INFO_SENTIMENT_APPLIED_V1"))
        {
            bool hasSymbol = ap.Payload.TryGetProperty("symbol", out var symEl) && symEl.ValueKind==JsonValueKind.String;
            bool hasFrom = ap.Payload.TryGetProperty("scaled_from", out var fromEl) && fromEl.ValueKind==JsonValueKind.Number;
            bool hasTo = ap.Payload.TryGetProperty("scaled_to", out var toEl) && toEl.ValueKind==JsonValueKind.Number;
            bool hasReason = ap.Payload.TryGetProperty("reason", out var rEl) && rEl.ValueKind==JsonValueKind.String;
            if (!(hasSymbol && hasFrom && hasTo && hasReason)) V("missing_field", ap.Seq, hasSymbol? symEl.GetString():null, ap.Ts.ToString("O"), "APPLIED missing required fields");
            if (req.sentimentMode=="shadow") V("mode_violation", ap.Seq, hasSymbol? symEl.GetString():null, ap.Ts.ToString("O"), "APPLIED not allowed in shadow mode");
        }

        // Numeric invariants on trades
        var tradeLines = File.ReadAllLines(req.TradesPath).Where(l=>!string.IsNullOrWhiteSpace(l)).ToList();
        if (tradeLines.Count>1)
        {
            for (int i=1;i<tradeLines.Count;i++)
            {
                var parts = tradeLines[i].Split(',');
                if (parts.Length < 13) { V("trade_row",0,null,null,$"trade line {i+1} malformed"); continue; }
                string pnlCcy = parts[7];
                if (pnlCcy.Contains('E') || pnlCcy.Contains('e')) V("numeric_format",0,parts[2],parts[0],"scientific notation in pnl_ccy");
                if (pnlCcy.Contains(',')) V("numeric_format",0,parts[2],parts[0],"comma decimal disallowed");
                // volume integer
                if (!long.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) V("numeric_format",0,parts[2],parts[0],"volume_units not integer");
            }
        }

        // Symbol superset: events symbols must contain all trade symbols
    var evSymbols = parsed.Select(p=>GetSym(p.Payload)).Where(s=>!string.IsNullOrWhiteSpace(s)).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tradeSymbols = tradeLines.Skip(1).Select(l=> l.Split(',')[2]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tsSym in tradeSymbols)
            if (!evSymbols.Contains(tsSym)) V("symbol_set",0,tsSym,null,"trade symbol missing from events");

        // DATA_QA_ABORT gating: if present ensure no BAR/SENTIMENT after abort seq
        var abort = parsed.FirstOrDefault(p=>p.Type=="DATA_QA_ABORT_V1");
        if (abort!=null)
        {
            foreach (var later in parsed.Where(p=>p.Seq>abort.Seq && (p.Type.StartsWith("INFO_SENTIMENT_") || p.Type=="BAR_V1")))
                V("order_violation", later.Seq, GetSym(later.Payload), later.Ts.ToString("O"), "event after QA_ABORT");
        }

        violations = violations
            .OrderBy(v=>v.Sequence)
            .ThenBy(v=>v.Kind,StringComparer.Ordinal)
            .ToList();
        int exit = violations.Count==0 ? 0 : 2; // 0 ok, 2 fail; 1 reserved for runtime errors
        var json = JsonSerializer.Serialize(new {
            schema = schema,
            summary = new { @checked = new { events = parsed.Count, trades = tradeLines.Count-1 }, violations = violations.Count },
            violations = violations.Select(v=> new { kind = v.Kind, seq = v.Sequence, symbol = v.Symbol, ts = v.Ts, detail = v.Detail }).ToArray()
        }, new JsonSerializerOptions{WriteIndented=true});
        return new StrictVerifyReport(exit, violations, json);
    }

    private static string? GetSym(JsonElement payload)
    {
        if (payload.ValueKind!=JsonValueKind.Object) return null;
        if (payload.TryGetProperty("symbol", out var s) && s.ValueKind==JsonValueKind.String) return s.GetString();
        if (payload.TryGetProperty("InstrumentId", out var i) && i.ValueKind==JsonValueKind.Object && i.TryGetProperty("Value", out var v) && v.ValueKind==JsonValueKind.String) return v.GetString();
        return null;
    }

    private static string UnwrapCsvQuoted(string raw)
    {
        if (raw.Length>=2 && raw[0]=='"' && raw[^1]=='"')
        {
            var inner = raw.Substring(1, raw.Length-2);
            return inner.Replace("\"\"", "\"");
        }
        return raw;
    }
    private static string[] SplitCsv(string line)
    {
        var result = new List<string>(); var sb = new StringBuilder(); bool inQ=false;
        for (int i=0;i<line.Length;i++)
        {
            char c=line[i];
            if (inQ)
            {
                if (c=='"')
                {
                    if (i+1<line.Length && line[i+1]=='"'){ sb.Append('"'); i++; }
                    else inQ=false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c==','){ result.Add(sb.ToString()); sb.Clear(); }
                else if (c=='"') inQ=true; else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }
}
