using System;
using System.Collections.Generic;
using System.Linq;

namespace TiYf.Engine.Core;

public sealed record NewsEvent(DateTime Utc, string Impact, List<string> Tags)
{
    public bool MatchesInstrument(string instrument)
    {
        if (Tags is null || Tags.Count == 0) return false;
        var (baseCode, quoteCode) = InstrumentToCurrencies(instrument);
        return Tags.Any(tag => string.Equals(tag, baseCode, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(tag, quoteCode, StringComparison.OrdinalIgnoreCase));
    }

    private static (string Base, string Quote) InstrumentToCurrencies(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument) || instrument.Length < 6)
        {
            return (instrument.ToUpperInvariant(), string.Empty);
        }

        var trimmed = instrument.Trim().ToUpperInvariant();
        return (trimmed.Substring(0, 3), trimmed.Substring(trimmed.Length - 3, 3));
    }
}
