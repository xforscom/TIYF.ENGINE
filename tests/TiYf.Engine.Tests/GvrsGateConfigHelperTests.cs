using System.Text.Json;
using TiYf.Engine.Core;
using Xunit;

namespace TiYf.Engine.Tests;

public class GvrsGateConfigHelperTests
{
    [Fact]
    public void Defaults_Disabled_WhenMissing()
    {
        using var doc = JsonDocument.Parse("{}");

        var config = GvrsGateConfigHelper.Resolve(doc);

        Assert.False(config.Enabled);
        Assert.False(config.BlockOnVolatile);
    }

    [Fact]
    public void Parses_BlockFlag_WhenPresent()
    {
        var json = """
        {
          "gvrs_gate": {
            "enabled": true,
            "block_on_volatile": true
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var config = GvrsGateConfigHelper.Resolve(doc);

        Assert.True(config.Enabled);
        Assert.True(config.BlockOnVolatile);
    }

    [Fact]
    public void EnabledWithoutBlock_RemainsTelemetryOnly()
    {
        var json = """
        {
          "gvrs_gate": {
            "enabled": true
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var config = GvrsGateConfigHelper.Resolve(doc);

        Assert.True(config.Enabled);
        Assert.False(config.BlockOnVolatile);
    }
}
