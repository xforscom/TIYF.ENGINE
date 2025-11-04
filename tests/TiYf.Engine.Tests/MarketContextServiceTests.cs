using System;
using System.Collections.Generic;
using TiYf.Engine.Core;
using Xunit;

namespace TiYf.Engine.Tests;

public class MarketContextServiceTests
{
    private static Bar CreateBar(string symbol, DateTime startUtc, decimal open, decimal high, decimal low, decimal close)
        => new(new InstrumentId(symbol), startUtc, startUtc.AddHours(1), open, high, low, close, 1m);

    [Fact]
    public void FxAtrPercentileProducesVolatileBucket()
    {
        var config = new GlobalVolatilityGateConfig(
            "shadow",
            0.0m,
            0.3m,
            new List<GlobalVolatilityComponentConfig>
            {
                new("fx_atr_percentile", 1.0m)
            });

        var service = new MarketContextService(config, atrLookbackHours: 3, atrPercentileHours: 3, proxyLookbackHours: 3);

        var start = DateTime.UtcNow.Date;
        service.OnBar(CreateBar("EURUSD", start, 1.0m, 1.02m, 0.98m, 1.01m), BarInterval.OneHour);
        service.OnBar(CreateBar("EURUSD", start.AddHours(1), 1.01m, 1.05m, 0.97m, 1.03m), BarInterval.OneHour);
        service.OnBar(CreateBar("EURUSD", start.AddHours(2), 1.03m, 1.12m, 0.90m, 1.10m), BarInterval.OneHour);

        Assert.True(service.HasValue);
        Assert.InRange(service.CurrentRaw, 0.9m, 1.0m);
        Assert.Equal("volatile", service.CurrentBucket);
    }

    [Fact]
    public void RiskProxyZProducesModerateBucket()
    {
        var config = new GlobalVolatilityGateConfig(
            "shadow",
            0.0m,
            0.5m,
            new List<GlobalVolatilityComponentConfig>
            {
                new("risk_proxy_z", 1.0m)
            });

        var service = new MarketContextService(config, atrLookbackHours: 3, atrPercentileHours: 3, proxyLookbackHours: 4);

        var start = DateTime.UtcNow.Date;
        service.OnBar(CreateBar("SPX500_USD", start, 4000m, 4005m, 3995m, 4000m), BarInterval.OneHour);
        service.OnBar(CreateBar("SPX500_USD", start.AddHours(1), 4000m, 4002m, 3998m, 3999m), BarInterval.OneHour);
        service.OnBar(CreateBar("SPX500_USD", start.AddHours(2), 3999m, 4000m, 3996m, 3997m), BarInterval.OneHour);
        service.OnBar(CreateBar("SPX500_USD", start.AddHours(3), 3997m, 3999m, 3995m, 3996m), BarInterval.OneHour);

        Assert.True(service.HasValue);
        Assert.InRange(service.CurrentRaw, -0.5m, 0.5m);
        Assert.Equal("moderate", service.CurrentBucket);
    }

    [Fact]
    public void EwmaSeedsWithFirstRaw()
    {
        var config = new GlobalVolatilityGateConfig(
            "shadow",
            0.0m,
            0.4m,
            new List<GlobalVolatilityComponentConfig>
            {
                new("fx_atr_percentile", 1.0m)
            });

        var service = new MarketContextService(config, atrLookbackHours: 3, atrPercentileHours: 3, proxyLookbackHours: 3);

        var start = DateTime.UtcNow.Date;
        service.OnBar(CreateBar("EURUSD", start, 1.0m, 1.01m, 0.99m, 1.005m), BarInterval.OneHour);
        service.OnBar(CreateBar("EURUSD", start.AddHours(1), 1.005m, 1.015m, 0.995m, 1.008m), BarInterval.OneHour);
        service.OnBar(CreateBar("EURUSD", start.AddHours(2), 1.008m, 1.050m, 0.980m, 1.020m), BarInterval.OneHour);

        Assert.True(service.HasValue);
        Assert.Equal(service.CurrentRaw, service.CurrentEwma);
    }

    [Fact]
    public void EvaluateShadowHitProducesAlert()
    {
        var config = new GlobalVolatilityGateConfig(
            "shadow",
            1.1m,
            0.3m,
            new List<GlobalVolatilityComponentConfig>
            {
                new("fx_atr_percentile", 1.0m)
            });

        var service = new MarketContextService(config, atrLookbackHours: 3, atrPercentileHours: 3, proxyLookbackHours: 3);

        var start = DateTime.UtcNow.Date;
        service.OnBar(CreateBar("EURUSD", start, 1.0m, 1.02m, 0.98m, 1.01m), BarInterval.OneHour);
        service.OnBar(CreateBar("EURUSD", start.AddHours(1), 1.01m, 1.05m, 0.97m, 1.03m), BarInterval.OneHour);
        service.OnBar(CreateBar("EURUSD", start.AddHours(2), 1.03m, 1.12m, 0.90m, 1.10m), BarInterval.OneHour);

        var evaluation = service.Evaluate(config);
        Assert.True(evaluation.ShouldAlert);
        Assert.Equal("Volatile", evaluation.Bucket);
        Assert.Equal("shadow", evaluation.Mode);

        var manager = new GvrsShadowAlertManager();
        Assert.True(manager.TryRegister("DEC-1", evaluation.ShouldAlert));
        Assert.False(manager.TryRegister("DEC-1", evaluation.ShouldAlert));
    }

    [Fact]
    public void EvaluateShadowHitReturnsFalseWhenAboveThreshold()
    {
        var config = new GlobalVolatilityGateConfig(
            "shadow",
            -0.5m,
            0.3m,
            new List<GlobalVolatilityComponentConfig>
            {
                new("fx_atr_percentile", 1.0m)
            });

        var service = new MarketContextService(config, atrLookbackHours: 3, atrPercentileHours: 3, proxyLookbackHours: 3);

        var start = DateTime.UtcNow.Date;
        service.OnBar(CreateBar("EURUSD", start, 1.0m, 1.02m, 0.98m, 1.01m), BarInterval.OneHour);
        service.OnBar(CreateBar("EURUSD", start.AddHours(1), 1.01m, 1.05m, 0.97m, 1.03m), BarInterval.OneHour);
        service.OnBar(CreateBar("EURUSD", start.AddHours(2), 1.03m, 1.12m, 0.90m, 1.10m), BarInterval.OneHour);

        var evaluation = service.Evaluate(config);
        Assert.False(evaluation.ShouldAlert);
        var manager = new GvrsShadowAlertManager();
        Assert.False(manager.TryRegister("DEC-2", evaluation.ShouldAlert));
    }

    [Fact]
    public void ShadowAlertManagerClearAllowsReemit()
    {
        var manager = new GvrsShadowAlertManager();
        Assert.True(manager.TryRegister("DEC-99", shouldAlert: true));
        Assert.False(manager.TryRegister("DEC-99", shouldAlert: true));

        manager.Clear("DEC-99");
        Assert.True(manager.TryRegister("DEC-99", shouldAlert: true));
    }
}
