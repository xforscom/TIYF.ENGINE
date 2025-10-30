using System;

namespace TiYf.Engine.Sim;

/// <summary>
/// Normalization helpers for OANDA instrument symbols so we maintain a single source of truth
/// for both API requests and internal engine identifiers.
/// </summary>
public static class OandaInstrumentNormalizer
{
    /// <summary>
    /// Converts an instrument string into the canonical engine representation (e.g. EURUSD).
    /// </summary>
    public static string? ToCanonical(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var text = raw.Trim().ToUpperInvariant();
        text = text.Replace("_", string.Empty, StringComparison.Ordinal);
        text = text.Replace("/", string.Empty, StringComparison.Ordinal);
        return text;
    }

    /// <summary>
    /// Converts an instrument string into the format expected by the OANDA streaming API (e.g. EUR_USD).
    /// </summary>
    public static string? ToApiSymbol(string? raw)
    {
        var canonical = ToCanonical(raw);
        if (string.IsNullOrWhiteSpace(canonical)) return canonical;
        if (canonical.Length == 6)
        {
            return $"{canonical[..3]}_{canonical[3..]}";
        }
        return canonical;
    }
}
