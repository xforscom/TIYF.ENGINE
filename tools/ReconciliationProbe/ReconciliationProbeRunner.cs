using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ReconciliationProbe;

public sealed record ReconciliationProbeResult(
    int TotalRecords,
    int MismatchesTotal,
    IReadOnlyDictionary<string, int> MismatchesByReason,
    IReadOnlyCollection<string> SymbolsWithMismatch,
    DateTime? LastReconcileUtc);

public static class ReconciliationProbeRunner
{
    public static ReconciliationProbeResult Analyze(string root, string? adapterFilter = null, string? accountFilter = null)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Root must be provided.", nameof(root));
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Reconciliation root not found: {root}");
        }

        var files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var reasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime? lastUtc = null;
        var total = 0;
        var mismatches = 0;

        foreach (var file in files)
        {
            using var reader = new StreamReader(file);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(line);
                var rootEl = doc.RootElement;

                if (!MatchesFilter(rootEl, "adapter", adapterFilter))
                {
                    continue;
                }

                if (!MatchesFilter(rootEl, "account_id", accountFilter))
                {
                    continue;
                }

                total++;
                var utc = TryParseUtc(rootEl);
                if (utc.HasValue && (!lastUtc.HasValue || utc.Value > lastUtc.Value))
                {
                    lastUtc = utc;
                }

                var status = NormalizeStatus(rootEl.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null);
                if (!string.Equals(status, "mismatch", StringComparison.Ordinal))
                {
                    continue;
                }

                mismatches++;
                var reason = NormalizeReason(rootEl.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : null);
                reasonCounts[reason] = reasonCounts.TryGetValue(reason, out var current) ? current + 1 : 1;
                var symbol = NormalizeSymbol(rootEl.TryGetProperty("symbol", out var symbolProp) ? symbolProp.GetString() : null);
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    symbols.Add(symbol);
                }
            }
        }

        return new ReconciliationProbeResult(
            total,
            mismatches,
            new ReadOnlyDictionary<string, int>(reasonCounts
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)),
            new ReadOnlyCollection<string>(symbols.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray()),
            lastUtc);
    }

    public static void WriteArtifacts(ReconciliationProbeResult result, string outputDir)
    {
        if (result is null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (string.IsNullOrWhiteSpace(outputDir))
        {
            throw new ArgumentException("Output directory must be provided.", nameof(outputDir));
        }

        Directory.CreateDirectory(outputDir);
        var summaryLine = BuildSummaryLine(result);
        File.WriteAllText(Path.Combine(outputDir, "summary.txt"), summaryLine + Environment.NewLine);
        File.WriteAllText(Path.Combine(outputDir, "metrics.txt"), BuildMetrics(result));

        var reasons = result.MismatchesByReason.Keys.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray();
        var health = new
        {
            last_reconcile_utc = result.LastReconcileUtc,
            mismatches_total = result.MismatchesTotal,
            mismatch_reasons = reasons
        };
        var json = JsonSerializer.Serialize(health, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDir, "health.json"), json);
    }

    private static string BuildSummaryLine(ReconciliationProbeResult result)
    {
        var reasons = result.MismatchesByReason.Count == 0
            ? "none"
            : string.Join(' ', result.MismatchesByReason
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => $"{kvp.Key}={kvp.Value}"));

        var timestamp = result.LastReconcileUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "n/a";
        return $"reconciliation_summary: total_records={result.TotalRecords} mismatches_total={result.MismatchesTotal} symbols_with_mismatch={result.SymbolsWithMismatch.Count} reasons=[{reasons}] last_reconcile_utc={timestamp}";
    }

    private static string BuildMetrics(ReconciliationProbeResult result)
    {
        var builder = new StringBuilder();
        AppendMetric(builder, "reconciler_records_total", result.TotalRecords);
        AppendMetric(builder, "reconciler_mismatches_total", result.MismatchesTotal);
        AppendMetric(builder, "reconciler_symbols_mismatched_total", result.SymbolsWithMismatch.Count);
        if (result.LastReconcileUtc.HasValue)
        {
            var unix = new DateTimeOffset(result.LastReconcileUtc.Value, TimeSpan.Zero).ToUnixTimeSeconds();
            AppendMetric(builder, "reconciler_last_reconcile_ts", unix);
        }
        foreach (var kvp in result.MismatchesByReason.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            AppendMetric(builder, "reconciler_mismatches_by_reason", kvp.Value, "reason", kvp.Key);
        }
        return builder.ToString();
    }

    private static void AppendMetric(StringBuilder builder, string name, long value)
    {
        builder.Append(name)
            .Append(' ')
            .Append(value.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
    }

    private static void AppendMetric(StringBuilder builder, string name, long value, string labelName, string labelValue)
    {
        builder.Append(name)
            .Append('{')
            .Append(labelName)
            .Append("=\"")
            .Append(labelValue.Replace("\"", "\\\"", StringComparison.Ordinal))
            .Append("\"} ")
            .Append(value.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
    }

    private static bool MatchesFilter(JsonElement element, string propertyName, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        var value = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        return string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime? TryParseUtc(JsonElement root)
    {
        if (!root.TryGetProperty("utc_ts", out var tsProp))
        {
            return null;
        }

        if (tsProp.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(tsProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        return null;
    }

    private static string NormalizeStatus(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string NormalizeReason(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(normalized) ? "unknown" : normalized;
    }

    private static string NormalizeSymbol(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}
