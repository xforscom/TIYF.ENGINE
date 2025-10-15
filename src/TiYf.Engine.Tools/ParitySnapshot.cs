using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TiYf.Engine.Tools;

public static class ParitySnapshot
{
    public sealed record ParityDiff(int Line, string? A, string? B);

    public sealed record ParitySection(bool Match, string HashA, string HashB, ParityDiff? FirstDiff);

    public sealed record ParitySnapshotResult(ParitySection Events, ParitySection? Trades, int ExitCode);

    public static ParitySnapshotResult Compute(string eventsA, string eventsB, string? tradesA, string? tradesB)
    {
        var events = ComputeSection(eventsA, eventsB, NormalizeEvents);
        ParitySection? trades = null;
        if (!string.IsNullOrWhiteSpace(tradesA) && !string.IsNullOrWhiteSpace(tradesB))
        {
            trades = ComputeSection(tradesA!, tradesB!, NormalizeTrades);
        }
        var exit = events.Match && (trades?.Match ?? true) ? 0 : 2;
        return new ParitySnapshotResult(events, trades, exit);
    }

    private static ParitySection ComputeSection(string pathA, string pathB, Func<string, List<string>> normalizer)
    {
        var normA = normalizer(pathA);
        var normB = normalizer(pathB);
        var hashA = HashJoin(normA);
        var hashB = HashJoin(normB);
        var match = string.Equals(hashA, hashB, StringComparison.Ordinal);
        ParityDiff? diff = null;
        if (!match)
        {
            var max = Math.Max(normA.Count, normB.Count);
            for (int i = 0; i < max; i++)
            {
                var a = i < normA.Count ? normA[i] : "<EOF>A";
                var b = i < normB.Count ? normB[i] : "<EOF>B";
                if (!string.Equals(a, b, StringComparison.Ordinal))
                {
                    diff = new ParityDiff(i + 1, a, b);
                    break;
                }
            }
        }
        return new ParitySection(match, hashA, hashB, diff);
    }

    private static string HashJoin(IEnumerable<string> lines)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var joined = string.Join('\n', lines);
        var bytes = System.Text.Encoding.UTF8.GetBytes(joined);
        return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static List<string> NormalizeEvents(string path)
    {
        var lines = File.ReadAllLines(path).ToList();
        if (lines.Count == 0) return lines;
        if (lines[0].StartsWith("schema_version=", StringComparison.OrdinalIgnoreCase))
            lines.RemoveAt(0);
        return lines;
    }

    private static List<string> NormalizeTrades(string path)
    {
        var lines = File.ReadAllLines(path).ToList();
        if (lines.Count == 0) return lines;
        if (lines[0].StartsWith("schema_version=", StringComparison.OrdinalIgnoreCase))
            lines.RemoveAt(0);
        if (lines.Count == 0) return lines;
        var header = lines[0];
        var cols = header.Split(',');
        var cfgIdx = Array.FindIndex(cols, c => string.Equals(c.Trim(), "config_hash", StringComparison.OrdinalIgnoreCase));
        if (cfgIdx >= 0)
        {
            for (int i = 1; i < lines.Count; i++)
            {
                var parts = lines[i].Split(',');
                if (parts.Length > cfgIdx)
                {
                    parts[cfgIdx] = string.Empty;
                    lines[i] = string.Join(',', parts);
                }
            }
        }
        return lines;
    }
}
