using System;
using System.Collections.Generic;
using TiYf.Engine.Core.Slippage;

namespace TiYf.Engine.Tests;

public sealed class SlippageModelTests
{
    [Fact]
    public void FixedBpsModel_AppliesSpread_ForBuy()
    {
        var profile = new SlippageProfile(
            "fixed_bps",
            new FixedBpsSlippageProfile(DefaultBps: 0.5m, Instruments: new Dictionary<string, decimal> { ["EURUSD"] = 1.0m }));
        var model = SlippageModelFactory.Create(profile);
        var intent = 1.0800m;
        var adjusted = model.Apply(intent, isBuy: true, "EURUSD", 1000, DateTime.UtcNow);
        var expected = intent + (intent * (0.0001m)); // 1 bps
        Assert.Equal(expected, adjusted);
    }

    [Fact]
    public void FixedBpsModel_Widens_ForSell()
    {
        var profile = new SlippageProfile(
            "fixed_bps",
            new FixedBpsSlippageProfile(DefaultBps: 2m));
        var model = SlippageModelFactory.Create(profile);
        var intent = 1950m;
        var adjusted = model.Apply(intent, isBuy: false, "XAUUSD", 100, DateTime.UtcNow);
        var expected = intent - (intent * (2m / 10_000m));
        Assert.Equal(expected, adjusted);
    }

    [Fact]
    public void ZeroModel_RemainsUnchanged()
    {
        var model = SlippageModelFactory.Create(null);
        var price = 1.25m;
        Assert.Equal(price, model.Apply(price, true, "EURUSD", 1000, DateTime.UtcNow));
    }
}
