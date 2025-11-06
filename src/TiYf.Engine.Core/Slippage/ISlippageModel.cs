using System;

namespace TiYf.Engine.Core.Slippage;

/// <summary>
/// Provides an execution price estimate based on intent and trade context.
/// </summary>
public interface ISlippageModel
{
    decimal Apply(decimal intendedPrice, bool isBuy, string instrumentId, long units, DateTime utcNow);
}
