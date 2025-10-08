using System.Globalization;
using System.Text.Json;

namespace TiYf.Engine.Core;

public sealed record DataQaConfig(
    bool Enabled,
    int MaxMissingBarsPerInstrument,
    bool AllowDuplicates,
    decimal SpikeZ,
    int ForwardFillBars,
    bool DropSpikes
);

public sealed record DataQaIssue(string Symbol, string Kind, DateTime Ts, string Details);
public sealed record DataQaResult(bool Passed, int SymbolsChecked, int Issues, int Repaired, IReadOnlyList<DataQaIssue> IssuesList);

public static class DataQaAnalyzer
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    public static DataQaResult Run(DataQaConfig cfg, Dictionary<string,List<(DateTime Ts, decimal Price)>> ticksBySymbol)
    {
        var issues = new List<DataQaIssue>();
        int repaired = 0;
        foreach (var (symbol, list) in ticksBySymbol.OrderBy(k=>k.Key, StringComparer.Ordinal))
        {
            if (list.Count == 0) continue;
            // Sort deterministically
            var ordered = list.OrderBy(t=>t.Ts).ToList();
            // Build bar minute starts (distinct)
            var barStarts = ordered.Select(t => AlignMinute(t.Ts)).Distinct().OrderBy(t=>t).ToList();
            // Missing bars
            for (int i=1;i<barStarts.Count;i++)
            {
                var gapMinutes = (int)((barStarts[i] - barStarts[i-1]).TotalMinutes);
                if (gapMinutes > 1)
                {
                    for (int g=1; g<gapMinutes; g++)
                    {
                        var missingTs = barStarts[i-1].AddMinutes(g);
                        issues.Add(new DataQaIssue(symbol, "missing_bar", missingTs, "gap"));
                    }
                }
            }
            // Duplicate tick timestamps
            for (int i=1;i<ordered.Count;i++)
            {
                if (ordered[i].Ts == ordered[i-1].Ts && !cfg.AllowDuplicates)
                {
                    issues.Add(new DataQaIssue(symbol, "duplicate", ordered[i].Ts, "duplicate_tick_ts"));
                }
            }
            // Spike detection via rolling deltas
            var closesByMinute = ordered
                .GroupBy(t => AlignMinute(t.Ts))
                .Select(g => (Minute: g.Key, Close: g.Last().Price))
                .OrderBy(x=>x.Minute)
                .ToList();
            var deltas = new List<decimal>();
            for (int i=1;i<closesByMinute.Count;i++)
            {
                var delta = closesByMinute[i].Close - closesByMinute[i-1].Close;
                if (deltas.Count >= 5)
                {
                    var mean = deltas.Average();
                    var variance = deltas.Count>0 ? deltas.Sum(d => (d-mean)*(d-mean)) / deltas.Count : 0m;
                    var std = variance > 0 ? (decimal)Math.Sqrt((double)variance) : 0m;
                    if (std > 0m)
                    {
                        var z = Math.Abs(std) > 0 ? (double)Math.Abs(delta - mean) / (double)std : 0d;
                        if ((decimal)z > cfg.SpikeZ)
                        {
                            issues.Add(new DataQaIssue(symbol, "spike", closesByMinute[i].Minute, string.Create(CultureInfo.InvariantCulture, $"delta={delta},mean={mean},std={std}")));
                            if (cfg.DropSpikes)
                            {
                                repaired++;
                                // Skip adding this delta to rolling stats for deterministic repair effect
                                continue;
                            }
                        }
                    }
                }
                deltas.Add(delta);
                if (deltas.Count > 100) deltas.RemoveAt(0); // cap window to prevent unbounded growth
            }
        }
        // Pass criteria
        bool passed = true;
        if (issues.Any(i=> i.Kind=="missing_bar"))
        {
            // Count missing per symbol
            foreach (var grp in issues.Where(i=>i.Kind=="missing_bar").GroupBy(i=>i.Symbol))
            {
                if (grp.Count() > cfg.MaxMissingBarsPerInstrument) { passed=false; break; }
            }
        }
        if (passed && issues.Any(i=> i.Kind=="duplicate") && !cfg.AllowDuplicates) passed = false;
        if (passed && issues.Any(i=> i.Kind=="spike" && !cfg.DropSpikes)) passed = false; // unrepaired spikes fail
        return new DataQaResult(passed, ticksBySymbol.Count, issues.Count, repaired, issues);
    }

    private static DateTime AlignMinute(DateTime ts) => new(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0, DateTimeKind.Utc);
}
