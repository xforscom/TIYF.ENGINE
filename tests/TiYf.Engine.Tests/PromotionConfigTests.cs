using System.Linq;
using System.Text.Json;
using TiYf.Engine.Core;
using Xunit;

namespace TiYf.Engine.Tests;

public class PromotionConfigTests
{
    [Fact]
    public void Parse_WithFullConfiguration_MapsAllFields()
    {
        using var doc = JsonDocument.Parse("""
        {
          "promotion": {
            "enabled": true,
            "shadow_candidates": ["strategyA", "strategyB"],
            "probation_days": 45,
            "min_trades": 120,
            "promotion_threshold": 0.72,
            "demotion_threshold": 0.28
          }
        }
        """);

        var config = RiskConfigParser.Parse(doc.RootElement).Promotion;

        Assert.True(config.Enabled);
        Assert.Equal(new[] { "strategyA", "strategyB" }, config.ShadowCandidates);
        Assert.Equal(45, config.ProbationDays);
        Assert.Equal(120, config.MinTrades);
        Assert.Equal(0.72m, config.PromotionThreshold);
        Assert.Equal(0.28m, config.DemotionThreshold);
        Assert.False(string.IsNullOrWhiteSpace(config.ConfigHash));
    }

    [Fact]
    public void Parse_WithMinimalConfiguration_UsesDefaults()
    {
        using var doc = JsonDocument.Parse("""
        {
          "promotion": {
            "enabled": true
          }
        }
        """);

        var config = RiskConfigParser.Parse(doc.RootElement).Promotion;

        Assert.True(config.Enabled);
        Assert.Empty(config.ShadowCandidates);
        Assert.Equal(30, config.ProbationDays);
        Assert.Equal(50, config.MinTrades);
        Assert.Equal(0.6m, config.PromotionThreshold);
        Assert.Equal(0.4m, config.DemotionThreshold);
        Assert.False(string.IsNullOrWhiteSpace(config.ConfigHash));
    }

    [Fact]
    public void PromotionHash_IsStableAcrossEquivalentJson()
    {
        const string jsonA = """
        {
          "promotion": {
            "enabled": false,
            "promotion_threshold": 0.55,
            "demotion_threshold": 0.35,
            "shadow_candidates": ["alpha", "beta"],
            "min_trades": 60,
            "probation_days": 40
          }
        }
        """;

        const string jsonB = """
        {
          "promotion" : {   "min_trades" : 60, "shadow_candidates" : [
              "alpha",
              "beta"
            ],
            "probation_days" : 40,
            "promotion_threshold" : 0.55,
            "demotion_threshold" : 0.35,
            "enabled" : false
          }
        }
        """;

        using var docA = JsonDocument.Parse(jsonA);
        using var docB = JsonDocument.Parse(jsonB);

        var promoA = RiskConfigParser.Parse(docA.RootElement).Promotion;
        var promoB = RiskConfigParser.Parse(docB.RootElement).Promotion;

        Assert.Equal(promoA.ConfigHash, promoB.ConfigHash);
        Assert.Equal(promoA.Enabled, promoB.Enabled);
        Assert.Equal(promoA.ShadowCandidates.ToArray(), promoB.ShadowCandidates.ToArray());
        Assert.Equal(promoA.ProbationDays, promoB.ProbationDays);
        Assert.Equal(promoA.MinTrades, promoB.MinTrades);
        Assert.Equal(promoA.PromotionThreshold, promoB.PromotionThreshold);
        Assert.Equal(promoA.DemotionThreshold, promoB.DemotionThreshold);
    }
}
