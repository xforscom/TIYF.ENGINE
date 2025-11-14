using System.Text.Json;
using TiYf.Engine.Core;
using Xunit;

namespace TiYf.Engine.Tests;

public class RiskConfigParserTests
{
    [Fact]
    public void RiskConfig_ParsesCamelAndSnakeCase_ValuesEqual()
    {
        var snake = JsonDocument.Parse("""{ "real_leverage_cap": 30, "margin_usage_cap_pct": 900, "per_position_risk_cap_pct": 5, "basket_mode": "instrument_bucket", "enable_scale_to_fit": true, "enforcement_enabled": false, "lot_step": 0.1 }""");
        var camel = JsonDocument.Parse("""{ "realLeverageCap": 30, "marginUsageCapPct": 900, "perPositionRiskCapPct": 5, "basketMode": "instrument_bucket", "enableScaleToFit": true, "enforcementEnabled": false, "lotStep": 0.1 }""");

        var rcSnake = RiskConfigParser.Parse(snake.RootElement);
        var rcCamel = RiskConfigParser.Parse(camel.RootElement);

        Assert.Equal(rcSnake.RealLeverageCap, rcCamel.RealLeverageCap);
        Assert.Equal(rcSnake.MarginUsageCapPct, rcCamel.MarginUsageCapPct);
        Assert.Equal(rcSnake.PerPositionRiskCapPct, rcCamel.PerPositionRiskCapPct);
        Assert.Equal(rcSnake.BasketMode, rcCamel.BasketMode);
        Assert.Equal(rcSnake.EnableScaleToFit, rcCamel.EnableScaleToFit);
        Assert.Equal(rcSnake.EnforcementEnabled, rcCamel.EnforcementEnabled);
        Assert.Equal(rcSnake.LotStep, rcCamel.LotStep);
        Assert.False(rcSnake.Promotion.Enabled);
        Assert.False(rcCamel.Promotion.Enabled);
        Assert.Empty(rcSnake.Promotion.ShadowCandidates);
        Assert.Equal(rcSnake.Promotion.ConfigHash, rcCamel.Promotion.ConfigHash);
    }

    [Fact]
    public void NewsBlackout_ParsesHttpSource()
    {
        using var document = JsonDocument.Parse("""
        {
          "news_blackout": {
            "enabled": true,
            "minutes_before": 10,
            "minutes_after": 20,
            "poll_seconds": 45,
            "source_type": "http",
            "http": {
              "base_uri": "https://example.test/feed",
              "api_key_header": "X-Auth",
              "api_key_env": "NEWS_TOKEN",
              "headers": { "Accept": "application/json" },
              "query": { "channel": "fx" }
            }
          }
        }
        """);

        var config = RiskConfigParser.Parse(document.RootElement);
        var news = config.NewsBlackout;
        Assert.NotNull(news);
        Assert.Equal("http", news!.SourceType);
        Assert.NotNull(news.Http);
        Assert.Equal("https://example.test/feed", news.Http!.BaseUri);
        Assert.Equal("X-Auth", news.Http.ApiKeyHeaderName);
        Assert.Equal("NEWS_TOKEN", news.Http.ApiKeyEnvVar);
        Assert.Equal("application/json", news.Http.Headers?["Accept"]);
        Assert.Equal("fx", news.Http.QueryParameters?["channel"]);
    }
}
