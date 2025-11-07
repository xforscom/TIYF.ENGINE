using System.Collections.Generic;

namespace TiYf.Engine.Host;

public readonly record struct PromotionTelemetrySnapshot(
    IReadOnlyList<string> Candidates,
    int ProbationDays,
    int MinTrades,
    decimal PromotionThreshold,
    decimal DemotionThreshold)
{
    public int CandidatesTotal => Candidates?.Count ?? 0;
}
