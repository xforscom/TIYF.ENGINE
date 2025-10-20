using TiYf.Engine.Host;
using Xunit;

namespace TiYf.Engine.Tests;

public class EngineHostStateTests
{
    [Fact]
    public void CreateHealthPayload_ReturnsExpectedShape()
    {
        var state = new EngineHostState("ctrader-demo", new[] { "dataQa=active", "riskProbe=disabled" });
        state.MarkConnected(true);
        state.SetLastDecision(DateTime.UtcNow.AddMinutes(-1));
        state.UpdateLag(123.4);
        state.UpdatePendingOrders(2);
        state.SetLastLog("Connected to cTrader endpoint");

        var payload = state.CreateHealthPayload();
        var dict = payload.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(payload));

        Assert.Equal("ctrader-demo", dict["adapter"]);
        Assert.True((bool)dict["connected"]!);
        Assert.NotNull(dict["last_h1_decision_utc"]);
        Assert.Equal(123.4, dict["bar_lag_ms"]);
        Assert.Equal(2, dict["pending_orders"]);

        var flags = Assert.IsAssignableFrom<string[]>(dict["feature_flags"]!);
        Assert.Contains("dataQa=active", flags);
        Assert.Contains("riskProbe=disabled", flags);

        Assert.NotNull(dict["last_heartbeat_utc"]);
        Assert.Equal("Connected to cTrader endpoint", dict["last_log"]);
    }
}
