using System;
using TiYf.Engine.Core.Slippage;
using Xunit;

namespace TiYf.Engine.Tests;

public class SessionPipSlippageModelTests
{
    [Theory]
    [InlineData(0, "asia")]
    [InlineData(8, "eu_open")]
    [InlineData(14, "us_open")]
    [InlineData(20, "overnight")]
    public void ResolvesSessionByHour(int hour, string expectedBucket)
    {
        var profile = new SessionSlippageProfile(
            DefaultPips: 0.5m,
            SessionPips: new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                [expectedBucket] = 1.0m
            });
        var model = new SessionPipSlippageModel(profile);
        var ts = new DateTime(2025, 1, 1, hour, 0, 0, DateTimeKind.Utc);

        var price = model.Apply(1.2000m, isBuy: true, instrumentId: "EURUSD", units: 1_000, utcNow: ts);

        Assert.NotEqual(1.2000m, price);
    }
}
