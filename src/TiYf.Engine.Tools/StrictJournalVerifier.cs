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
        "DATA_QA_BEGIN_V1","DATA_QA_ISSUE_V1","DATA_QA_SUMMARY_V1","DATA_QA_ABORT_V1",
        "PENALTY_APPLIED_V1",
        "INFO_RISK_EVAL_V1","ALERT_BLOCK_NET_EXPOSURE","ALERT_BLOCK_DRAWDOWN"
    };

    private static readonly HashSet<string> AllowedAdapters = new(StringComparer.OrdinalIgnoreCase)
    {
        "stub",
        "ctrader-demo",
        "ctrader-live",
        "oanda-demo",
        "oanda-live"
    };

    private sealed record ParsedEvent(ulong Seq, DateTime Ts, string Type, JsonElement Payload);

    public static StrictVerifyReport Verify(StrictVerifyRequest req)
    {
        var violations = new List<StrictViolation>();
        void V(string kind, ulong seq, string? sym, string? ts, string detail) { violations.Add(new StrictViolation(kind, seq, sym, ts, detail)); }

        if (!File.Exists(req.EventsPath)) throw new FileNotFoundException(req.EventsPath);
        if (!File.Exists(req.TradesPath)) throw new FileNotFoundException(req.TradesPath);

        // Parse events
        var lines = File.ReadAllLines(req.EventsPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count < 2) V("file_format", 0, null, null, "events missing meta/header");
        string schema = "";
        string configHash = "";
        string adapterId = "";
        string brokerId = "";
        string accountId = "";
        string[] headerCols = Array.Empty<string>();
        if (lines.Count >= 1)
        {
            var metaParts = lines[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in metaParts)
            {
                var kv = p.Split('=');
                if (kv.Length != 2) continue;
                switch (kv[0])
                {
                    case "schema_version":
                        schema = kv[1];
                        break;
                    case "config_hash":
                        configHash = kv[1];
                        break;
                    case "adapter_id":
                        adapterId = kv[1];
                        break;
                    case "broker":
                        brokerId = kv[1];
                        break;
                    case "account_id":
                        accountId = kv[1];
                        break;
                }
            }
        }
        if (lines.Count >= 2)
        {
            headerCols = SplitCsv(lines[1]);
        }
        if (string.IsNullOrWhiteSpace(schema) || string.Compare(schema, req.MinimumSchema, StringComparison.Ordinal) < 0)
            V("schema_version", 0, null, null, $"schema_version {schema} < required {req.MinimumSchema}");
        if (string.IsNullOrWhiteSpace(configHash)) V("meta", 0, null, null, "config_hash missing in meta");
        if (string.IsNullOrWhiteSpace(adapterId)) V("meta", 0, null, null, "adapter_id missing in meta");
        else if (!AllowedAdapters.Contains(adapterId)) V("meta", 0, null, null, $"adapter_id '{adapterId}' not permitted");
        if (string.IsNullOrWhiteSpace(brokerId)) V("meta", 0, null, null, "broker missing in meta");
        if (string.IsNullOrWhiteSpace(accountId)) V("meta", 0, null, null, "account_id missing in meta");

        int adapterIdx = Array.IndexOf(headerCols, "src_adapter");
        if (adapterIdx < 0) V("header", 0, null, null, "src_adapter column missing in events header");
        int payloadIdx = Array.IndexOf(headerCols, "payload_json");
        if (payloadIdx < 0) V("header", 0, null, null, "payload_json column missing in events header");

        var parsed = new List<ParsedEvent>();
        ulong lastSeq = 0; DateTime lastTs = DateTime.MinValue;
        for (int i = 2; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] cols = SplitCsv(line);
            if ((payloadIdx < 0 || adapterIdx < 0) && cols.Length < 4) { V("row_format", 0, null, null, $"line {i + 1} malformed"); continue; }
            if (!ulong.TryParse(cols[0], out var seq)) { V("sequence_parse", 0, null, null, $"line {i + 1} sequence invalid"); continue; }
            var tsRaw = cols[1]; var evtType = cols[2];
            if (adapterIdx >= 0)
            {
                if (adapterIdx >= cols.Length) { V("row_format", seq, null, tsRaw, "src_adapter column missing"); continue; }
                var rowAdapter = cols[adapterIdx];
                if (string.IsNullOrWhiteSpace(rowAdapter)) V("src_adapter", seq, null, tsRaw, "src_adapter empty");
                else if (!string.IsNullOrWhiteSpace(adapterId) && !string.Equals(rowAdapter, adapterId, StringComparison.OrdinalIgnoreCase))
                    V("src_adapter", seq, null, tsRaw, $"src_adapter '{rowAdapter}' != meta '{adapterId}'");
            }
            if (payloadIdx >= 0 && payloadIdx >= cols.Length) { V("row_format", seq, null, tsRaw, "payload_json column missing"); continue; }
            var payloadRaw = payloadIdx >= 0 && payloadIdx < cols.Length ? UnwrapCsvQuoted(cols[payloadIdx]) : string.Empty;
            if (seq <= lastSeq) V("order_violation", seq, null, tsRaw, "non-increasing sequence"); else lastSeq = seq;
            if (!DateTime.TryParse(tsRaw, null, DateTimeStyles.RoundtripKind, out var ts) || ts.Kind != DateTimeKind.Utc) V("timestamp", seq, null, tsRaw, "invalid or non-UTC timestamp");
            else if (ts < lastTs) V("order_violation", seq, null, tsRaw, "timestamp regression"); else lastTs = ts;
            if (!KnownEventTypes.Contains(evtType)) { V("unknown_event", seq, null, tsRaw, evtType); continue; }
            JsonElement payload;
            try { using var doc = JsonDocument.Parse(payloadRaw); payload = doc.RootElement.Clone(); }
            catch { V("payload_json", seq, null, tsRaw, "invalid JSON"); continue; }
            parsed.Add(new ParsedEvent(seq, ts, evtType, payload));
        }

        if (req.strict)
        {
            // Ordering rules relative placement within bar (approx by looking for sequences): enforce pattern BAR -> Z -> CLAMP? -> APPLIED?
            foreach (var ev in parsed)
            {
                if (ev.Type == "INFO_SENTIMENT_Z_V1")
                {
                    var prev = parsed.FirstOrDefault(p => p.Seq == ev.Seq - 1);
                    if (prev == null || prev.Type != "BAR_V1") V("order_violation", ev.Seq, GetSym(ev.Payload), ev.Ts.ToString("O"), "Z must immediately follow BAR");
                }
                else if (ev.Type == "INFO_SENTIMENT_CLAMP_V1")
                {
                    var prev = parsed.FirstOrDefault(p => p.Seq == ev.Seq - 1);
                    if (prev == null || prev.Type != "INFO_SENTIMENT_Z_V1") V("order_violation", ev.Seq, GetSym(ev.Payload), ev.Ts.ToString("O"), "CLAMP must follow Z");
                }
                else if (ev.Type == "INFO_SENTIMENT_APPLIED_V1")
                {
                    var prev = parsed.FirstOrDefault(p => p.Seq == ev.Seq - 1);
                    if (prev == null || (prev.Type != "INFO_SENTIMENT_Z_V1" && prev.Type != "INFO_SENTIMENT_CLAMP_V1")) V("order_violation", ev.Seq, GetSym(ev.Payload), ev.Ts.ToString("O"), "APPLIED must follow Z or CLAMP");
                }
                else if (ev.Type == "PENALTY_APPLIED_V1")
                {
                    var prev = parsed.FirstOrDefault(p => p.Seq == ev.Seq - 1);
                    // Allow penalty after BAR, any sentiment event (Z/CLAMP/APPLIED), or a risk evaluation
                    if (prev == null || (prev.Type != "BAR_V1" && !prev.Type.StartsWith("INFO_SENTIMENT_", StringComparison.Ordinal) && prev.Type != "INFO_RISK_EVAL_V1"))
                        V("order_violation", ev.Seq, GetSym(ev.Payload), ev.Ts.ToString("O"), "PENALTY must follow BAR, sentiment, or risk eval");
                }
                else if (ev.Type == "INFO_RISK_EVAL_V1")
                {
                    // Must follow BAR or a sentiment event and precede any ALERT_BLOCK_* (implicitly by sequence check when alert examined)
                    var prev = parsed.FirstOrDefault(p => p.Seq == ev.Seq - 1);
                    if (prev == null || (prev.Type != "BAR_V1" && !prev.Type.StartsWith("INFO_SENTIMENT_", StringComparison.Ordinal)))
                        V("order_violation", ev.Seq, GetSym(ev.Payload), ev.Ts.ToString("O"), "RISK_EVAL must follow BAR or sentiment");
                }
                else if (ev.Type.StartsWith("ALERT_BLOCK_", StringComparison.Ordinal))
                {
                    var prev = parsed.FirstOrDefault(p => p.Seq == ev.Seq - 1);
                    if (prev == null || (prev.Type != "INFO_RISK_EVAL_V1" && !prev.Type.StartsWith("ALERT_BLOCK_", StringComparison.Ordinal)))
                        V("order_violation", ev.Seq, GetSym(ev.Payload), ev.Ts.ToString("O"), "ALERT_BLOCK must follow INFO_RISK_EVAL or previous ALERT_BLOCK");
                }
            }
        }

        // Field requirements for INFO_SENTIMENT_APPLIED_V1
        foreach (var ap in parsed.Where(p => p.Type == "INFO_SENTIMENT_APPLIED_V1"))
        {
            bool hasSymbol = ap.Payload.TryGetProperty("symbol", out var symEl) && symEl.ValueKind == JsonValueKind.String;
            bool hasFrom = ap.Payload.TryGetProperty("scaled_from", out var fromEl) && fromEl.ValueKind == JsonValueKind.Number;
            bool hasTo = ap.Payload.TryGetProperty("scaled_to", out var toEl) && toEl.ValueKind == JsonValueKind.Number;
            bool hasReason = ap.Payload.TryGetProperty("reason", out var rEl) && rEl.ValueKind == JsonValueKind.String;
            if (!(hasSymbol && hasFrom && hasTo && hasReason)) V("missing_field", ap.Seq, hasSymbol ? symEl.GetString() : null, ap.Ts.ToString("O"), "APPLIED missing required fields");
            if (req.sentimentMode == "shadow") V("mode_violation", ap.Seq, hasSymbol ? symEl.GetString() : null, ap.Ts.ToString("O"), "APPLIED not allowed in shadow mode");
        }

        // Field requirements for PENALTY_APPLIED_V1 (active)
        foreach (var pen in parsed.Where(p => p.Type == "PENALTY_APPLIED_V1"))
        {
            bool hasSymbol = pen.Payload.TryGetProperty("symbol", out var sEl) && sEl.ValueKind == JsonValueKind.String;
            bool hasReason = pen.Payload.TryGetProperty("reason", out var rsEl) && rsEl.ValueKind == JsonValueKind.String;
            bool hasOrig = pen.Payload.TryGetProperty("original_units", out var oEl) && oEl.ValueKind == JsonValueKind.Number;
            bool hasAdj = pen.Payload.TryGetProperty("adjusted_units", out var aEl) && aEl.ValueKind == JsonValueKind.Number;
            bool hasScalar = pen.Payload.TryGetProperty("penalty_scalar", out var scEl) && scEl.ValueKind == JsonValueKind.Number;
            if (!(hasSymbol && hasReason && hasOrig && hasAdj && hasScalar)) V("missing_field", pen.Seq, hasSymbol ? sEl.GetString() : null, pen.Ts.ToString("O"), "PENALTY missing required fields");
            // Numeric formatting invariant: no scientific notation, no commas
            foreach (var num in new[] { oEl, aEl, scEl })
            {
                var raw = num.GetRawText();
                if (raw.Contains('E') || raw.Contains('e')) V("numeric_format", pen.Seq, hasSymbol ? sEl.GetString() : null, pen.Ts.ToString("O"), "scientific notation in penalty field");
                if (raw.Contains(',')) V("numeric_format", pen.Seq, hasSymbol ? sEl.GetString() : null, pen.Ts.ToString("O"), "comma decimal disallowed in penalty field");
            }
        }

        // Field requirements for INFO_RISK_EVAL_V1
        foreach (var rv in parsed.Where(p => p.Type == "INFO_RISK_EVAL_V1"))
        {
            bool hasSymbol = rv.Payload.TryGetProperty("symbol", out var sEl) && sEl.ValueKind == JsonValueKind.String;
            bool hasTs = rv.Payload.TryGetProperty("ts", out var tsEl) && (tsEl.ValueKind == JsonValueKind.String || tsEl.ValueKind == JsonValueKind.Number);
            bool hasNet = rv.Payload.TryGetProperty("net_exposure", out var neEl) && neEl.ValueKind == JsonValueKind.Number;
            bool hasDd = rv.Payload.TryGetProperty("run_drawdown", out var ddEl) && ddEl.ValueKind == JsonValueKind.Number;
            if (!(hasSymbol && hasTs && hasNet && hasDd)) V("missing_field", rv.Seq, hasSymbol ? sEl.GetString() : null, rv.Ts.ToString("O"), "RISK_EVAL missing required fields");
        }
        // Field requirements + cross rules for ALERT_BLOCK_* events
        foreach (var ab in parsed.Where(p => p.Type.StartsWith("ALERT_BLOCK_", StringComparison.Ordinal)))
        {
            if (ab.Type == "ALERT_BLOCK_NET_EXPOSURE")
            {
                bool hasSymbol = ab.Payload.TryGetProperty("symbol", out var sEl) && sEl.ValueKind == JsonValueKind.String;
                bool hasLimit = ab.Payload.TryGetProperty("limit", out var lEl) && lEl.ValueKind == JsonValueKind.Number;
                bool hasVal = ab.Payload.TryGetProperty("value", out var vEl) && vEl.ValueKind == JsonValueKind.Number;
                bool hasReason = ab.Payload.TryGetProperty("reason", out var rEl) && rEl.ValueKind == JsonValueKind.String;
                if (!(hasSymbol && hasLimit && hasVal && hasReason)) V("missing_field", ab.Seq, hasSymbol ? sEl.GetString() : null, ab.Ts.ToString("O"), "ALERT_BLOCK_NET_EXPOSURE missing fields");
            }
            else if (ab.Type == "ALERT_BLOCK_DRAWDOWN")
            {
                bool hasLimit = ab.Payload.TryGetProperty("limit_ccy", out var lEl) && lEl.ValueKind == JsonValueKind.Number;
                bool hasVal = ab.Payload.TryGetProperty("value_ccy", out var vEl) && vEl.ValueKind == JsonValueKind.Number;
                bool hasReason = ab.Payload.TryGetProperty("reason", out var rEl) && rEl.ValueKind == JsonValueKind.String;
                if (!(hasLimit && hasVal && hasReason)) V("missing_field", ab.Seq, null, ab.Ts.ToString("O"), "ALERT_BLOCK_DRAWDOWN missing fields");
            }
        }

        // Numeric invariants & provenance on trades
        var tradeLines = File.ReadAllLines(req.TradesPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var tradeHeader = tradeLines.Count > 0 ? tradeLines[0].Split(',') : Array.Empty<string>();
        int tradeSchemaIdx = Array.IndexOf(tradeHeader, "schema_version");
        int tradeConfigIdx = Array.IndexOf(tradeHeader, "config_hash");
        int tradeAdapterIdx = Array.IndexOf(tradeHeader, "src_adapter");
        if (tradeSchemaIdx < 0) V("header", 0, null, null, "schema_version column missing in trades header");
        if (tradeConfigIdx < 0) V("header", 0, null, null, "config_hash column missing in trades header");
        if (tradeAdapterIdx < 0) V("header", 0, null, null, "src_adapter column missing in trades header");
        if (tradeLines.Count > 1)
        {
            for (int i = 1; i < tradeLines.Count; i++)
            {
                var parts = tradeLines[i].Split(',');
                if (parts.Length < 14) { V("trade_row", 0, null, null, $"trade line {i + 1} malformed"); continue; }
                var tradeTs = parts.Length > 0 ? parts[0] : string.Empty;
                var tradeSymbol = parts.Length > 2 ? parts[2] : string.Empty;
                string pnlCcy = parts[7];
                if (pnlCcy.Contains('E') || pnlCcy.Contains('e')) V("numeric_format", 0, tradeSymbol, tradeTs, "scientific notation in pnl_ccy");
                if (pnlCcy.Contains(',')) V("numeric_format", 0, tradeSymbol, tradeTs, "comma decimal disallowed");
                if (!long.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) V("numeric_format", 0, tradeSymbol, tradeTs, "volume_units not integer");
                if (tradeAdapterIdx >= 0)
                {
                    if (tradeAdapterIdx >= parts.Length) V("trade_row", 0, tradeSymbol, tradeTs, "trade src_adapter column missing");
                    else
                    {
                        var tradeAdapter = parts[tradeAdapterIdx];
                        if (string.IsNullOrWhiteSpace(tradeAdapter)) V("src_adapter", 0, tradeSymbol, tradeTs, "trade src_adapter empty");
                        else if (!string.IsNullOrWhiteSpace(adapterId) && !string.Equals(tradeAdapter, adapterId, StringComparison.OrdinalIgnoreCase))
                            V("src_adapter", 0, tradeSymbol, tradeTs, $"trade src_adapter '{tradeAdapter}' != meta '{adapterId}'");
                    }
                }
                if (tradeConfigIdx >= 0 && tradeConfigIdx < parts.Length && !string.IsNullOrWhiteSpace(configHash) && !string.Equals(parts[tradeConfigIdx], configHash, StringComparison.OrdinalIgnoreCase))
                    V("config_hash_mismatch", 0, tradeSymbol, tradeTs, $"trade config_hash '{parts[tradeConfigIdx]}' != meta '{configHash}'");
                if (tradeSchemaIdx >= 0 && tradeSchemaIdx < parts.Length && !string.IsNullOrWhiteSpace(schema) && !string.Equals(parts[tradeSchemaIdx], schema, StringComparison.OrdinalIgnoreCase))
                    V("schema_mismatch", 0, tradeSymbol, tradeTs, $"trade schema_version '{parts[tradeSchemaIdx]}' != events '{schema}'");
            }
        }

        // Symbol superset: events symbols must contain all trade symbols
        var evSymbols = parsed.Select(p => GetSym(p.Payload)).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tradeSymbols = tradeLines.Skip(1).Select(l => l.Split(',')[2]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tsSym in tradeSymbols)
            if (!evSymbols.Contains(tsSym)) V("symbol_set", 0, tsSym, null, "trade symbol missing from events");

        // DATA_QA_ABORT gating: if present ensure no BAR/SENTIMENT after abort seq
        var abort = parsed.FirstOrDefault(p => p.Type == "DATA_QA_ABORT_V1");
        if (abort != null)
        {
            foreach (var later in parsed.Where(p => p.Seq > abort.Seq && (p.Type.StartsWith("INFO_SENTIMENT_") || p.Type == "BAR_V1")))
                V("order_violation", later.Seq, GetSym(later.Payload), later.Ts.ToString("O"), "event after QA_ABORT");
        }

        violations = violations
            .OrderBy(v => v.Sequence)
            .ThenBy(v => v.Kind, StringComparer.Ordinal)
            .ToList();
        int exit = violations.Count == 0 ? 0 : 2; // 0 ok, 2 fail; 1 reserved for runtime errors
        var json = JsonSerializer.Serialize(new
        {
            schema = schema,
            summary = new { @checked = new { events = parsed.Count, trades = tradeLines.Count - 1 }, violations = violations.Count },
            violations = violations.Select(v => new { kind = v.Kind, seq = v.Sequence, symbol = v.Symbol, ts = v.Ts, detail = v.Detail }).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true });
        return new StrictVerifyReport(exit, violations, json);
    }

    private static string? GetSym(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (payload.TryGetProperty("symbol", out var s) && s.ValueKind == JsonValueKind.String) return s.GetString();
        if (payload.TryGetProperty("InstrumentId", out var i) && i.ValueKind == JsonValueKind.Object && i.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    private static string UnwrapCsvQuoted(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            var inner = raw.Substring(1, raw.Length - 2);
            return inner.Replace("\"\"", "\"");
        }
        return raw;
    }
    private static string[] SplitCsv(string line)
    {
        var result = new List<string>(); var sb = new StringBuilder(); bool inQ = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQ)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQ = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else if (c == '"') inQ = true; else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }
}
