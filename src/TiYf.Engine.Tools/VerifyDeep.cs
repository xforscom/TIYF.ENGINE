using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiYf.Engine.Core.Infrastructure;

namespace TiYf.Engine.Tools;

public sealed record DeepVerifyOptions(
    string EventsPath,
    string TradesPath,
    string MinimumSchema,
    bool StrictOrdering,
    int MaxErrors,
    bool ReportDuplicates,
    string? SentimentMode
);

public sealed record DeepVerifySummary(
    bool JournalOk,
    bool StrictOk,
    IReadOnlyList<(string Key, string Reason)> JournalErrors,
    IReadOnlyList<StrictViolation> StrictViolations,
    DeepVerifyStats Stats
)
{
    public bool Ok => JournalOk && StrictOk && !Stats.HasBlockingAlerts;
}

public sealed record DeepVerifyStats(
    string SchemaVersion,
    string? ConfigHash,
    string? DataVersion,
    int EventCount,
    int TradeCount,
    IReadOnlyDictionary<string, int> EventTypeCounts,
    IReadOnlyDictionary<string, int> AlertCounts,
    decimal TotalPnlCcy,
    bool HasBlockingAlerts,
    string EventsHash,
    string TradesHash
);

public sealed record DeepVerifyResult(int ExitCode, string HumanSummary, string JsonSummary);

public static class DeepVerifyEngine
{
    public static DeepVerifyResult Run(DeepVerifyOptions options)
    {
        if (!File.Exists(options.EventsPath)) throw new VerifyFatalException($"Events file not found: {options.EventsPath}");
        if (!File.Exists(options.TradesPath)) throw new VerifyFatalException($"Trades file not found: {options.TradesPath}");

        var journalVerify = VerifyEngine.Run(options.EventsPath, new VerifyOptions(options.MaxErrors, Json: true, ReportDuplicates: options.ReportDuplicates));
        var strictReport = StrictJournalVerifier.Verify(new StrictVerifyRequest(
            options.EventsPath,
            options.TradesPath,
            options.MinimumSchema,
            strict: options.StrictOrdering,
            sentimentMode: options.SentimentMode
        ));

        var journalErrors = ExtractJournalErrors(journalVerify.JsonOutput);
        var stats = BuildStats(options.EventsPath, options.TradesPath);

        bool journalOk = journalVerify.ExitCode == 0;
        bool strictOk = strictReport.ExitCode == 0;
        bool overallOk = journalOk && strictOk && !stats.HasBlockingAlerts;

        var summary = new DeepVerifySummary(journalOk, strictOk, journalErrors, strictReport.Violations, stats);
        var jsonSummary = BuildJson(summary);
        var humanSummary = BuildHuman(summary);

        int exitCode = overallOk ? 0 : 2;
        return new DeepVerifyResult(exitCode, humanSummary, jsonSummary);
    }

    private static List<(string Key, string Reason)> ExtractJournalErrors(string? json)
    {
        var list = new List<(string Key, string Reason)>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var err in errors.EnumerateArray())
                {
                    string key = err.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? string.Empty : string.Empty;
                    string reason = err.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? string.Empty : err.GetRawText();
                    list.Add((key, reason));
                }
            }
        }
        catch
        {
            // Ignore JSON parse errors; fall back to empty list
        }
        return list;
    }

    private static string BuildJson(DeepVerifySummary summary)
    {
        var obj = new
        {
            ok = summary.Ok,
            checks = new
            {
                journal = new
                {
                    ok = summary.JournalOk,
                    errorCount = summary.JournalErrors.Count,
                    errors = summary.JournalErrors.Select(e => new { key = e.Key, reason = e.Reason }).ToArray()
                },
                strict = new
                {
                    ok = summary.StrictOk,
                    violationCount = summary.StrictViolations.Count,
                    violations = summary.StrictViolations.Select(v => new
                    {
                        kind = v.Kind,
                        seq = v.Sequence,
                        symbol = v.Symbol,
                        ts = v.Ts,
                        detail = v.Detail
                    }).ToArray()
                }
            },
            stats = new
            {
                schema = summary.Stats.SchemaVersion,
                configHash = summary.Stats.ConfigHash,
                dataVersion = summary.Stats.DataVersion,
                events = summary.Stats.EventCount,
                trades = summary.Stats.TradeCount,
                eventTypes = summary.Stats.EventTypeCounts,
                alertTypes = summary.Stats.AlertCounts,
                totalPnlCcy = summary.Stats.TotalPnlCcy,
                hasBlockingAlerts = summary.Stats.HasBlockingAlerts,
                hashes = new { events = summary.Stats.EventsHash, trades = summary.Stats.TradesHash }
            }
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildHuman(DeepVerifySummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine(summary.Ok ? "DEEP VERIFY: OK" : "DEEP VERIFY: FAIL");
        sb.AppendLine($"  - Journal check: {(summary.JournalOk ? "OK" : "FAIL")} ({summary.JournalErrors.Count} issues)");
        sb.AppendLine($"  - Strict check: {(summary.StrictOk ? "OK" : "FAIL")} ({summary.StrictViolations.Count} violations)");
        sb.AppendLine($"  - Blocking alerts: {(summary.Stats.HasBlockingAlerts ? "present" : "none detected")}");
        if (!summary.JournalOk)
        {
            foreach (var err in summary.JournalErrors.Take(5))
                sb.AppendLine($"    * {err.Key}: {err.Reason}");
            if (summary.JournalErrors.Count > 5) sb.AppendLine($"    * ...(total {summary.JournalErrors.Count})");
        }
        if (!summary.StrictOk)
        {
            foreach (var v in summary.StrictViolations.Take(5))
                sb.AppendLine($"    * {v.Kind} seq={v.Sequence} detail={v.Detail}");
            if (summary.StrictViolations.Count > 5) sb.AppendLine($"    * ...(total {summary.StrictViolations.Count})");
        }
        if (summary.Stats.HasBlockingAlerts)
        {
            foreach (var kv in summary.Stats.AlertCounts)
                sb.AppendLine($"    * ALERT {kv.Key}: {kv.Value}");
        }
        sb.AppendLine($"  events={summary.Stats.EventCount} trades={summary.Stats.TradeCount} schema={summary.Stats.SchemaVersion}");
        return sb.ToString().TrimEnd();
    }

    private static DeepVerifyStats BuildStats(string eventsPath, string tradesPath)
    {
        using var sha = SHA256.Create();
        var eventAnalyzer = AnalyzeEvents(eventsPath, sha);
        var tradeAnalyzer = AnalyzeTrades(tradesPath, sha);

        var alertCounts = eventAnalyzer.EventTypeCounts
            .Where(kv => kv.Key.StartsWith("ALERT_BLOCK_", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        bool hasBlockingAlerts = alertCounts.Values.Sum() > 0;

        return new DeepVerifyStats(
            eventAnalyzer.SchemaVersion,
            eventAnalyzer.ConfigHash,
            eventAnalyzer.DataVersion,
            eventAnalyzer.EventCount,
            tradeAnalyzer.TradeCount,
            eventAnalyzer.EventTypeCounts,
            alertCounts,
            tradeAnalyzer.TotalPnlCcy,
            hasBlockingAlerts,
            eventAnalyzer.Hash,
            tradeAnalyzer.Hash
        );
    }

    private sealed record EventAnalysis(
        string SchemaVersion,
        string? ConfigHash,
        string? DataVersion,
        int EventCount,
        Dictionary<string, int> EventTypeCounts,
        string Hash
    );

    private sealed record TradeAnalysis(int TradeCount, decimal TotalPnlCcy, string Hash);

    private static EventAnalysis AnalyzeEvents(string path, HashAlgorithm hashAlg)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) throw new VerifyFatalException("Events journal missing meta/header");

        string meta = lines[0];
        string header = lines[1];
        var headerCols = SplitCsv(header);
        int typeIdx = Array.IndexOf(headerCols, "event_type");
        if (typeIdx < 0) throw new VerifyFatalException("event_type column missing in events journal");

        int payloadIdx = Array.IndexOf(headerCols, "payload_json");
        if (payloadIdx < 0) throw new VerifyFatalException("payload_json column missing in events journal");

        string schema = ExtractMeta(meta, "schema_version") ?? string.Empty;
        string? configHash = ExtractMeta(meta, "config_hash");
        string? dataVersion = ExtractMeta(meta, "data_version");

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int eventCount = 0;
        for (int i = 2; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            eventCount++;
            var cols = SplitCsv(line);
            if (typeIdx >= cols.Length) throw new VerifyFatalException($"event_type missing at line {i + 1}");
            string evt = cols[typeIdx];
            counts.TryGetValue(evt, out var total);
            counts[evt] = total + 1;
        }

        string hash = ComputeHashSkippingMeta(path, hashAlg, skipLines: 2);
        return new EventAnalysis(schema, configHash, dataVersion, eventCount, counts, hash);
    }

    private static TradeAnalysis AnalyzeTrades(string path, HashAlgorithm hashAlg)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) throw new VerifyFatalException("Trades journal empty");

        int tradeCount = Math.Max(0, lines.Length - 1);
        decimal totalPnl = 0m;
        for (int i = 1; i < lines.Length; i++)
        {
            var parts = SplitCsv(lines[i]);
            if (parts.Length < 8) throw new VerifyFatalException($"Trades row {i + 1} malformed");
            if (decimal.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var pnl))
                totalPnl += pnl;
        }

        string hash = ComputeHashSkippingMeta(path, hashAlg, skipLines: 1);
        return new TradeAnalysis(tradeCount, decimal.Round(totalPnl, 6, MidpointRounding.AwayFromZero), hash);
    }

    private static string? ExtractMeta(string metaLine, string key)
    {
        var parts = metaLine.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            var kv = p.Split('=');
            if (kv.Length == 2 && string.Equals(kv[0], key, StringComparison.Ordinal))
                return kv[1];
        }
        return null;
    }

    private static string ComputeHashSkippingMeta(string path, HashAlgorithm hashAlg, int skipLines)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, leaveOpen: true);
        var lines = new List<string>();
        string? line;
        int index = 0;
        while ((line = reader.ReadLine()) != null)
        {
            if (index >= skipLines) lines.Add(line);
            index++;
        }
        var joined = string.Join('\n', lines);
        var bytes = Encoding.UTF8.GetBytes(joined);
        var hash = hashAlg.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string[] SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == ',')
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }
}