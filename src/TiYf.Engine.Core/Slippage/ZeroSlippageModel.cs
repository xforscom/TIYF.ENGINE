using System;

namespace TiYf.Engine.Core.Slippage;

/// <summary>
/// Default slippage model that leaves the intended price unchanged.
/// </summary>
public sealed class ZeroSlippageModel : ISlippageModel
{
    public decimal Apply(decimal intendedPrice, bool isBuy, string instrumentId, long units, DateTime utcNow)
        => intendedPrice;
}

