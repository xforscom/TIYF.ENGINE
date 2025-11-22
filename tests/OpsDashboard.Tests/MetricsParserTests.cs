using OpsDashboard.Services;
using Xunit;

namespace OpsDashboard.Tests;

public class MetricsParserTests
{
    [Fact]
    public void ParsesMetricsIgnoringComments()
    {
        var raw = """
        # HELP engine_config_id config id
        # TYPE engine_config_id gauge
        engine_config_id{config_id="demo-oanda-v1"} 1
        engine_risk_blocks_total 0

        # bad line below
        something-without-space
        engine_alerts_total{category="risk"} 3
        """;

        var parser = new MetricsParser();
        var parsed = parser.Parse(raw);

        Assert.True(parsed.TryGetValue("engine_config_id", out var configSamples));
        Assert.True(parsed.TryGetValue("engine_risk_blocks_total", out _));
        Assert.True(parsed.TryGetValue("engine_alerts_total", out var alertSamples));
        Assert.Equal("demo-oanda-v1", configSamples.Single().Labels["config_id"]);
        Assert.Equal("1", configSamples.Single().Value);
        Assert.Equal("3", alertSamples.Single().Value);
    }
}
