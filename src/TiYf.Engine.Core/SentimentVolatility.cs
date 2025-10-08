using System.Globalization;

namespace TiYf.Engine.Core;

public sealed record SentimentGuardConfig(bool Enabled, int Window, decimal VolGuardSigma, string Mode);

public sealed record SentimentSample(string Symbol, DateTime Ts, decimal SRaw, decimal Z, decimal Sigma, bool Clamped);

public static class SentimentVolatilityGuard
{
    // Deterministic synthetic S: simple normalized close -> convert price to log-return style incremental series
    // Caller feeds in close price; we convert to a synthetic SRaw using deterministic transform.
    public static SentimentSample Compute(
        SentimentGuardConfig cfg,
        string symbol,
        DateTime ts,
        decimal close,
        Queue<decimal> window,
        out bool added)
    {
        added = false;
        // Transform: SRaw = ln(1 + (close % 1000)/1000) to bound values and deterministic across decimals
        // Avoid double rounding; use decimal math then cast to double only for log if needed.
        var frac = (close % 1000m) / 1000m; // stable for typical FX price ranges
        var sRaw = (decimal)Math.Log(1.0 + (double)frac);
        if (window.Count >= cfg.Window) window.Dequeue();
        window.Enqueue(sRaw); added = true;
        // Compute population mean & std (population variance denominator = N)
        decimal mean = 0m; decimal variance = 0m; int n = window.Count;
        if (n > 0)
        {
            mean = window.Sum();
            mean /= n;
            if (n > 0)
            {
                foreach (var v in window) variance += (v - mean) * (v - mean);
                variance /= n; // population variance
            }
        }
        var sigma = variance <= 0m ? 0m : (decimal)Math.Sqrt((double)variance);
        decimal z = 0m;
        if (sigma > 0m)
        {
            var latest = sRaw;
            z = (latest - mean) / sigma;
        }
        bool clamp = sigma > cfg.VolGuardSigma; // clamp when volatility (std) exceeds threshold
        // NOTE: clamp does not alter trading in shadow; event only.
        return new SentimentSample(symbol, ts, sRaw, z, sigma, clamp);
    }
}
