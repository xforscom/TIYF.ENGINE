using System;
using System.Linq;
using TiYf.Engine.Core;

namespace TiYf.Engine.Sim;

public sealed record PromotionShadowSnapshot(
    int PromotionsTotal,
    int DemotionsTotal,
    int TradeCount,
    decimal WinRatio,
    int ProbationDays,
    int MinTrades,
    decimal PromotionThreshold,
    decimal DemotionThreshold,
    string[] Candidates);

/// <summary>
/// Shadow-only promotion evaluator that tracks promotion/demotion readiness over a probation window
/// without affecting live routing. Counters increment only when newer trades are observed.
/// </summary>
public sealed class PromotionShadowRuntime
{
    private readonly PromotionConfig _config;
    private int _promotions;
    private int _demotions;
    private DateTime _lastProcessedCloseUtc = DateTime.MinValue;

    public PromotionShadowRuntime(PromotionConfig config)
    {
        _config = config ?? PromotionConfig.Default;
    }

    public PromotionShadowSnapshot Evaluate(PositionTracker? positions, DateTime evaluationUtc)
    {
        if (_config is null || !_config.Enabled)
        {
            return new PromotionShadowSnapshot(0, 0, 0, 0m, _config?.ProbationDays ?? 0, _config?.MinTrades ?? 0, _config?.PromotionThreshold ?? 0m, _config?.DemotionThreshold ?? 0m, _config?.ShadowCandidates.ToArray() ?? Array.Empty<string>());
        }

        var normalizedEval = evaluationUtc.Kind == DateTimeKind.Utc ? evaluationUtc : DateTime.SpecifyKind(evaluationUtc, DateTimeKind.Utc);
        var cutoff = _config.ProbationDays > 0 ? normalizedEval.AddDays(-_config.ProbationDays) : DateTime.MinValue;
        var trades = positions?.Completed ?? Array.Empty<CompletedTrade>();
        var filtered = trades
            .Where(t => DateTime.SpecifyKind(t.UtcTsClose, DateTimeKind.Utc) >= cutoff)
            .OrderBy(t => t.UtcTsClose)
            .ToList();
        var tradeCount = filtered.Count;
        decimal winRatio = 0m;
        if (tradeCount > 0)
        {
            var wins = filtered.Count(t => t.PnlCcy > 0m);
            winRatio = (decimal)wins / tradeCount;
        }

        var newestClose = tradeCount > 0 ? DateTime.SpecifyKind(filtered[^1].UtcTsClose, DateTimeKind.Utc) : (DateTime?)null;
        var hasNewerClose = newestClose.HasValue && newestClose.Value > _lastProcessedCloseUtc;
        var eligible = tradeCount >= _config.MinTrades && hasNewerClose;
        if (eligible)
        {
            if (winRatio >= _config.PromotionThreshold)
            {
                _promotions++;
            }
            else if (winRatio <= _config.DemotionThreshold)
            {
                _demotions++;
            }
            _lastProcessedCloseUtc = newestClose!.Value;
        }

        return new PromotionShadowSnapshot(
            _promotions,
            _demotions,
            tradeCount,
            winRatio,
            _config.ProbationDays,
            _config.MinTrades,
            _config.PromotionThreshold,
            _config.DemotionThreshold,
            _config.ShadowCandidates.ToArray());
    }
}
