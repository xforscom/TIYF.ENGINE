using System;

namespace TiYf.Engine.Core.Slippage;

public static class SlippageModelFactory
{
    public static ISlippageModel Create(string? name)
    {
        var normalized = Normalize(name);
        return normalized switch
        {
            "zero" => new ZeroSlippageModel(),
            null or "" => new ZeroSlippageModel(),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unsupported slippage model")
        };
    }

    public static string Normalize(string? name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? "zero"
            : name.Trim().ToLowerInvariant();
    }
}
