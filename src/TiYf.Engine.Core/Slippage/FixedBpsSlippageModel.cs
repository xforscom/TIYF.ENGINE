using System;
using System.Collections.Generic;

namespace TiYf.Engine.Core.Slippage;

/// <summary>
/// Applies deterministic slippage by widening fills a fixed number of basis points per instrument.
/// </summary>
public sealed class FixedBpsSlippageModel : ISlippageModel
{
    private readonly IReadOnlyDictionary<string, decimal> _bpsByInstrument;
    private readonly decimal _defaultBps;

    public FixedBpsSlippageModel(FixedBpsSlippageProfile profile)
    {
        profile ??= new FixedBpsSlippageProfile();
        _defaultBps = Math.Max(0m, profile.DefaultBps);
        if (profile.Instruments is null)
        {
            _bpsByInstrument = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var normalized = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in profile.Instruments)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key))
                {
                    continue;
                }

                normalized[kvp.Key.Trim()] = Math.Max(0m, kvp.Value);
            }
            _bpsByInstrument = normalized;
        }
    }

    public decimal Apply(decimal intendedPrice, bool isBuy, string instrumentId, long units, DateTime utcNow)
    {
        var bps = ResolveBps(instrumentId);
        if (bps <= 0m || intendedPrice == 0m)
        {
            return intendedPrice;
        }

        var delta = intendedPrice * (bps / 10_000m);
        if (delta == 0m)
        {
            return intendedPrice;
        }

        var direction = isBuy ? 1m : -1m;
        return intendedPrice + (delta * direction);
    }

    private decimal ResolveBps(string instrumentId)
    {
        if (!string.IsNullOrWhiteSpace(instrumentId) && _bpsByInstrument.TryGetValue(instrumentId, out var bps))
        {
            return bps;
        }

        return _defaultBps;
    }
}
