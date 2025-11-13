using System.Collections.Generic;

namespace TiYf.Engine.Core.Slippage;

public sealed record SlippageProfile(
    string Model = "zero",
    FixedBpsSlippageProfile? FixedBps = null);

public sealed record FixedBpsSlippageProfile(
    decimal DefaultBps = 0m,
    IReadOnlyDictionary<string, decimal>? Instruments = null);
