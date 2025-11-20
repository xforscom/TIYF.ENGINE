using System;

namespace TiYf.Engine.Core.Slippage;

public static class SlippageModelFactory
{
    public static ISlippageModel Create(SlippageProfile? profile, string? legacyName = null)
    {
        var normalized = Normalize(profile?.Model ?? legacyName);
        return normalized switch
        {
            "zero" => new ZeroSlippageModel(),
            "fixed_bps" => new FixedBpsSlippageModel(profile?.FixedBps ?? new FixedBpsSlippageProfile()),
            "session_pips" => new SessionPipSlippageModel(profile?.Session ?? new SessionSlippageProfile()),
            _ => throw new ArgumentOutOfRangeException("model", profile?.Model ?? legacyName, "Unsupported slippage model")
        };
    }

    public static string Normalize(string? name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? "zero"
            : name.Trim().ToLowerInvariant();
    }
}
