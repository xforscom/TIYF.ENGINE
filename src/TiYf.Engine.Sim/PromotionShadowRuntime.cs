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

public sealed class PromotionShadowRuntime
{
    private readonly PromotionConfig _config;
    private int _promotions;
    private int _demotions;
    private int _lastProcessedTradeCount;

    public PromotionShadowRuntime(PromotionConfig config)
    {
        _config = config ?? PromotionConfig.Default;
    }

    public PromotionShadowSnapshot Evaluate(PositionTracker? positions)
    {
        if (_config is null || !_config.Enabled)
        {
            return new PromotionShadowSnapshot(0, 0, 0, 0m, _config?.ProbationDays ?? 0, _config?.MinTrades ?? 0, _config?.PromotionThreshold ?? 0m, _config?.DemotionThreshold ?? 0m, _config?.ShadowCandidates.ToArray() ?? Array.Empty<string>());
        }

        var trades = positions?.Completed ?? Array.Empty<CompletedTrade>();
        var tradeCount = trades.Count;
        decimal winRatio = 0m;
        if (tradeCount > 0)
        {
            var wins = trades.Count(t => t.PnlCcy > 0m);
            winRatio = (decimal)wins / tradeCount;
        }

        var hasNewTrades = tradeCount > _lastProcessedTradeCount;
        if (hasNewTrades && tradeCount >= _config.MinTrades)
        {
            if (winRatio >= _config.PromotionThreshold)
            {
                _promotions++;
            }
            else if (winRatio <= _config.DemotionThreshold)
            {
                _demotions++;
            }
            _lastProcessedTradeCount = tradeCount;
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
