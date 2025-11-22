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

        Assert.True(parsed.ContainsKey("engine_config_id"));
        Assert.True(parsed.ContainsKey("engine_risk_blocks_total"));
        Assert.True(parsed.ContainsKey("engine_alerts_total"));
        Assert.Equal("demo-oanda-v1", parsed["engine_config_id"].Single().Labels["config_id"]);
        Assert.Equal("1", parsed["engine_config_id"].Single().Value);
        Assert.Equal("3", parsed["engine_alerts_total"].Single().Value);
    }
}
