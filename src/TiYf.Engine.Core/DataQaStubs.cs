namespace TiYf.Engine.Core;

// M4 Task 1: Temporary lightweight stubs to satisfy existing simulation build until
// full Data QA module is reintroduced/refactored. They provide only the public
// surface consumed by Program.cs and EngineLoop for compilation.

public sealed record DataQaConfig(bool Enabled, int MaxMissingBarsPerInstrument, bool AllowDuplicates, decimal SpikeZ, int ForwardFillBars, bool DropSpikes);
public sealed record DataQaIssue(string Symbol, System.DateTime Ts, string Kind, string Details);
public sealed record DataQaResult(bool Passed, int SymbolsChecked, int Issues, int Repaired, System.Collections.Generic.IReadOnlyList<DataQaIssue> IssuesList);

public static class DataQaAnalyzer
{
    public static DataQaResult Run(DataQaConfig cfg, System.Collections.Generic.Dictionary<string,System.Collections.Generic.List<(System.DateTime,decimal)>> ticks)
    {
        // Deterministic no-op analysis: always passes with zero issues for stub.
        return new DataQaResult(true, ticks.Count, 0, 0, System.Array.Empty<DataQaIssue>());
    }
}

public sealed class SentimentVolatilityGuard
{
    public static (string Symbol, decimal Z, decimal SRaw, decimal Sigma, System.DateTime Ts, bool Clamped) Compute(
        SentimentGuardConfig cfg, string symbol, System.DateTime ts, decimal price,
        System.Collections.Generic.Queue<decimal> window, out bool added)
    {
        added = false;
        if (window.Count >= cfg.Window) window.Dequeue();
        window.Enqueue(price); added = true;
        return (symbol, 0m, price, cfg.VolGuardSigma, ts, false);
    }
}