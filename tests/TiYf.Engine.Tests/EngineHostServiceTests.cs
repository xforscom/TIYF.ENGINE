using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TiYf.Engine.Host;
using TiYf.Engine.Sim;

namespace TiYf.Engine.Tests;

public class EngineHostServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WithAdapter_SetsConnected()
    {
        var state = new EngineHostState("ctrader-demo", Array.Empty<string>());
        var settings = new CTraderAdapterSettings(
            Mode: "ctrader-demo",
            BaseUri: new Uri("https://example.com"),
            TokenUri: new Uri("https://example.com/token"),
            ApplicationId: "app",
            ClientSecret: "secret",
            ClientId: "client",
            AccessToken: "token",
            RefreshToken: "refresh",
            AccountId: "account",
            Broker: "Spotware",
            UseMock: true,
            MaxOrderUnits: 100_000,
            MaxNotional: 1_000_000m,
            RequestTimeout: TimeSpan.FromSeconds(5),
            RetryInitialDelay: TimeSpan.FromMilliseconds(5),
            RetryMaxDelay: TimeSpan.FromMilliseconds(10),
            RetryMaxAttempts: 3,
            HandshakeEndpoint: "/connectivity",
            OrderEndpoint: "/orders");

        var adapter = new CTraderOpenApiExecutionAdapter(new HttpClient(), settings);
        using var provider = new ServiceCollection()
            .AddSingleton(settings)
            .AddSingleton<IConnectableExecutionAdapter>(adapter)
            .AddSingleton(adapter)
            .BuildServiceProvider();

        var options = Options.Create(new EngineHostOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(10) });
        var service = new EngineHostService(state, provider, NullLogger<EngineHostService>.Instance, options);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100);
            Assert.True(state.Connected);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithoutAdapter_Beats()
    {
        var state = new EngineHostState("stub", Array.Empty<string>());
        using var provider = new ServiceCollection().BuildServiceProvider();
        var options = Options.Create(new EngineHostOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(10) });
        var service = new EngineHostService(state, provider, NullLogger<EngineHostService>.Instance, options);

        var initialHeartbeat = state.LastHeartbeatUtc;
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100);
            Assert.True(state.Connected);
            Assert.True(state.LastHeartbeatUtc > initialHeartbeat);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithOandaAdapter_SetsConnected()
    {
        var state = new EngineHostState("oanda-demo", Array.Empty<string>());
        var settings = new OandaAdapterSettings(
            Mode: "oanda-demo",
            BaseUri: new Uri("https://example.com/v3/"),
            AccountId: "practice-account",
            AccessToken: "token",
            UseMock: true,
            MaxOrderUnits: 100_000,
            RequestTimeout: TimeSpan.FromSeconds(5),
            RetryInitialDelay: TimeSpan.FromMilliseconds(5),
            RetryMaxDelay: TimeSpan.FromMilliseconds(10),
            RetryMaxAttempts: 3,
            HandshakeEndpoint: "/accounts/{accountId}/summary",
            OrderEndpoint: "/accounts/{accountId}/orders",
            PositionsEndpoint: "/accounts/{accountId}/openPositions",
            PendingOrdersEndpoint: "/accounts/{accountId}/orders?state=PENDING");

        var adapter = new OandaRestExecutionAdapter(new HttpClient(), settings);
        using var provider = new ServiceCollection()
            .AddSingleton(settings)
            .AddSingleton<IConnectableExecutionAdapter>(adapter)
            .AddSingleton(adapter)
            .BuildServiceProvider();

        var options = Options.Create(new EngineHostOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(10) });
        var service = new EngineHostService(state, provider, NullLogger<EngineHostService>.Instance, options);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100);
            Assert.True(state.Connected);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
