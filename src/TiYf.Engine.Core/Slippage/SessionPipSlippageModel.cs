using System;
using System.Collections.Generic;

namespace TiYf.Engine.Core.Slippage;

/// <summary>
/// Applies a deterministic slippage in pips based on instrument and session buckets (UTC hour ranges).
/// </summary>
public sealed class SessionPipSlippageModel : ISlippageModel
{
    private readonly decimal _defaultPips;
    private readonly Dictionary<string, decimal> _sessionPips;
    private readonly Dictionary<string, decimal> _instrumentPips;

    public SessionPipSlippageModel(SessionSlippageProfile profile)
    {
        profile ??= new SessionSlippageProfile();
        _defaultPips = Math.Max(0m, profile.DefaultPips);
        _sessionPips = Normalize(profile.SessionPips);
        _instrumentPips = Normalize(profile.InstrumentPips);
    }

    public decimal Apply(decimal intendedPrice, bool isBuy, string instrumentId, long units, DateTime utcNow)
    {
        if (intendedPrice == 0m)
        {
            return intendedPrice;
        }

        var pips = ResolvePips(instrumentId, utcNow);
        if (pips <= 0m)
        {
            return intendedPrice;
        }

        var pipValue = intendedPrice / 10_000m;
        var delta = pipValue * pips;
        var direction = isBuy ? 1m : -1m;
        return intendedPrice + (delta * direction);
    }

    private decimal ResolvePips(string instrumentId, DateTime utcNow)
    {
        if (!string.IsNullOrWhiteSpace(instrumentId) && _instrumentPips.TryGetValue(instrumentId, out var instPips))
        {
            return instPips;
        }

        var bucket = ResolveSessionBucket(utcNow);
        if (_sessionPips.TryGetValue(bucket, out var sessionPips))
        {
            return sessionPips;
        }

        return _defaultPips;
    }

    private static string ResolveSessionBucket(DateTime utcNow)
    {
        var hour = utcNow.Hour;
        return hour switch
        {
            >= 0 and < 7 => "asia",
            >= 7 and < 12 => "eu_open",
            >= 12 and < 17 => "us_open",
            _ => "overnight"
        };
    }

    private static Dictionary<string, decimal> Normalize(IReadOnlyDictionary<string, decimal>? source)
    {
        if (source is null || source.Count == 0)
        {
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in source)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key))
            {
                continue;
            }

            dict[kvp.Key.Trim()] = Math.Max(0m, kvp.Value);
        }

        return dict;
    }
}
