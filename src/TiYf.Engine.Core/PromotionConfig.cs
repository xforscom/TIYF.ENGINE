using System;
using System.Collections.Generic;

namespace TiYf.Engine.Core;

public sealed record PromotionConfig(
    bool Enabled,
    IReadOnlyList<string> ShadowCandidates,
    int ProbationDays,
    int MinTrades,
    decimal PromotionThreshold,
    decimal DemotionThreshold,
    string ConfigHash)
{
    public static PromotionConfig Default { get; } = new(
        false,
        Array.Empty<string>(),
        30,
        50,
        0.6m,
        0.4m,
        string.Empty);
}
