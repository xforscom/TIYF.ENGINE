using System;
using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Host;
using Xunit;

namespace TiYf.Engine.Tests;

public class EnginePromotionTelemetryTests
{
    [Fact]
    public void PromotionTelemetry_SurfacesMetricsAndHealthBlock()
    {
        var state = new EngineHostState("demo", Array.Empty<string>());
        var promotion = new PromotionConfig(
            Enabled: true,
            ShadowCandidates: new[] { "alpha", "beta" },
            ProbationDays: 45,
            MinTrades: 90,
            PromotionThreshold: 0.72m,
            DemotionThreshold: 0.28m,
            ConfigHash: "hash-123");

        state.SetPromotionConfig(promotion);
        var snapshot = state.CreateMetricsSnapshot();
        var metrics = EngineMetricsFormatter.Format(snapshot);

        Assert.Contains("engine_promotion_candidates_total 2", metrics, StringComparison.Ordinal);
        Assert.Contains("engine_promotion_probation_days 45", metrics, StringComparison.Ordinal);
        Assert.Contains("engine_promotion_min_trades 90", metrics, StringComparison.Ordinal);
        Assert.Contains("engine_promotion_threshold 0.72", metrics, StringComparison.Ordinal);
        Assert.Contains("engine_demotion_threshold 0.28", metrics, StringComparison.Ordinal);

        var healthJson = JsonSerializer.Serialize(state.CreateHealthPayload());
        using var doc = JsonDocument.Parse(healthJson);
        var root = doc.RootElement;
        Assert.Equal("hash-123", root.GetProperty("promotion_config_hash").GetString());
        var promotionBlock = root.GetProperty("promotion");
        Assert.Equal(2, promotionBlock.GetProperty("candidates").GetArrayLength());
        Assert.Equal(45, promotionBlock.GetProperty("probation_days").GetInt32());
        Assert.Equal(90, promotionBlock.GetProperty("min_trades").GetInt32());
        Assert.Equal(0.72m, promotionBlock.GetProperty("promotion_threshold").GetDecimal());
        Assert.Equal(0.28m, promotionBlock.GetProperty("demotion_threshold").GetDecimal());
    }

    [Fact]
    public void PromotionTelemetry_NullsOutWhenHashMissing()
    {
        var state = new EngineHostState("demo", Array.Empty<string>());
        state.SetPromotionConfig(null);

        var healthJson = JsonSerializer.Serialize(state.CreateHealthPayload());
        using var doc = JsonDocument.Parse(healthJson);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("promotion", out var promotionBlock));
        Assert.Equal(JsonValueKind.Null, promotionBlock.ValueKind);
        Assert.Equal(string.Empty, root.GetProperty("promotion_config_hash").GetString());

        var metrics = EngineMetricsFormatter.Format(state.CreateMetricsSnapshot());
        Assert.DoesNotContain("engine_promotion_candidates_total", metrics, StringComparison.Ordinal);
    }
}
