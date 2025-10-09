using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TiYf.Engine.Tools
{
    public sealed record DeepViolation(string Kind, string Detail, int? Row = null);
    public sealed class DeepVerifyReport
    {
        public int ExitCode { get; init; }
        public List<DeepViolation> Violations { get; } = new();
        public string Json { get; init; } = string.Empty;
    }

    public static class DeepJournalVerifier
    {
        public static DeepVerifyReport Verify(string eventsPath, string tradesPath)
        {
            var violations = new List<DeepViolation>();
            try
            {
                // Events: meta + header
                var lines = SafeReadAll(eventsPath);
                if (lines.Count < 2)
                    violations.Add(new DeepViolation("events_too_short", "events.csv has fewer than 2 header lines"));
                else
                {
                    if (!lines[0].StartsWith("schema_version=", StringComparison.Ordinal))
                        violations.Add(new DeepViolation("meta_missing", "First line must start with schema_version="));
                    if (!string.Equals(lines[1], "sequence,utc_ts,event_type,payload_json", StringComparison.Ordinal))
                        violations.Add(new DeepViolation("header_mismatch", "Unexpected events header"));
                    // Monotonic sequence and UTC timestamps
                    ulong prev = 0;
                    for (int i = 2; i < lines.Count; i++)
                    {
                        var row = lines[i];
                        var parts = SplitCsv(row);
                        if (parts.Length < 4) { violations.Add(new DeepViolation("row_malformed", "Less than 4 columns", i+1)); continue; }
                        if (!ulong.TryParse(parts[0], out var seq)) { violations.Add(new DeepViolation("seq_parse", "Non-numeric sequence", i+1)); continue; }
                        if (seq <= prev) violations.Add(new DeepViolation("sequence_nonmonotonic", $"{seq} <= {prev}", i+1));
                        prev = seq;
                        if (!DateTime.TryParse(parts[1], null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts) || ts.Kind != DateTimeKind.Utc)
                            violations.Add(new DeepViolation("utc_ts_invalid", "Timestamp not UTC ISO-8601", i+1));
                    }
                }

                // Trades basic shape and formatting
                var tlines = SafeReadAll(tradesPath);
                if (tlines.Count == 0)
                    violations.Add(new DeepViolation("trades_missing", "trades.csv not found or empty"));
                else
                {
                    var header = tlines[0].Split(',');
                    int pnlIdx = Array.FindIndex(header, h => h.Equals("pnl_ccy", StringComparison.OrdinalIgnoreCase));
                    int volIdx = Array.FindIndex(header, h => h.Equals("volume_units", StringComparison.OrdinalIgnoreCase));
                    if (pnlIdx < 0 || volIdx < 0)
                        violations.Add(new DeepViolation("trades_header_missing", "Required columns missing"));
                    for (int i = 1; i < tlines.Count; i++)
                    {
                        var parts = tlines[i].Split(',');
                        if (pnlIdx < parts.Length)
                        {
                            var pnl = parts[pnlIdx];
                            if (pnl.Contains("E") || pnl.Contains("e")) violations.Add(new DeepViolation("numeric_format", "Scientific notation in pnl_ccy", i+1));
                            if (!decimal.TryParse(pnl, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) violations.Add(new DeepViolation("pnl_parse", "pnl_ccy not a decimal", i+1));
                        }
                        if (volIdx < parts.Length)
                        {
                            if (!long.TryParse(parts[volIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) violations.Add(new DeepViolation("units_parse", "volume_units not integer", i+1));
                        }
                    }
                }

                var ok = violations.Count == 0;
                var json = JsonSerializer.Serialize(new
                {
                    ok,
                    violations,
                    summary = new { events = new { rows = Math.Max(0, (int)lines.Count - 2) }, trades = new { rows = Math.Max(0, tlines.Count - 1) } }
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
                return new DeepVerifyReport { ExitCode = ok ? 0 : 2, Json = json, };
            }
            catch (Exception ex)
            {
                var json = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
                return new DeepVerifyReport { ExitCode = 1, Json = json };
            }

            static List<string> SafeReadAll(string path)
            {
                try
                {
                    var text = File.ReadAllText(path);
                    text = text.Replace("\r\n", "\n").Replace("\r", "\n");
                    return text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                }
                catch { return new List<string>(); }
            }

            static string[] SplitCsv(string line)
            {
                // simple CSV splitter respecting quotes
                var res = new List<string>();
                bool inQ = false; var sb = new System.Text.StringBuilder();
                for (int i=0;i<line.Length;i++)
                {
                    var c = line[i];
                    if (c=='"') { inQ = !inQ; sb.Append(c); }
                    else if (c==',' && !inQ) { res.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
                res.Add(sb.ToString());
                return res.ToArray();
            }
        }
    }
}
