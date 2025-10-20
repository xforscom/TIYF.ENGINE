using System.Net;
using System.Text;
using System.Text.Json;
using TiYf.Engine.Sim;
using Xunit;

namespace TiYf.Engine.Tests;

public class CTraderAdapterTests
{
    [Fact]
    public async Task ConnectAsync_WithMock_Succeeds()
    {
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
            RetryInitialDelay: TimeSpan.FromSeconds(1),
            RetryMaxDelay: TimeSpan.FromSeconds(2),
            RetryMaxAttempts: 3,
            HandshakeEndpoint: "/connectivity",
            OrderEndpoint: "/orders");

        var adapter = new CTraderOpenApiExecutionAdapter(new HttpClient(), settings);
        await adapter.ConnectAsync();
        var result = await adapter.ExecuteMarketAsync(new OrderRequest("DEC1", "EURUSD", TradeSide.Buy, 10, DateTime.UtcNow));

        Assert.True(result.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(result.BrokerOrderId));
    }

    [Fact]
    public async Task ExecuteMarketAsync_ParsesOrderAndFill()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        }));
        handler.Enqueue(async (req, token) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.NotNull(req.Content);
            var payload = await req.Content!.ReadAsStringAsync(token);
            Assert.Contains("\"symbol\":\"EURUSD\"", payload);
            var responseBody = "{\"orderId\":\"CT-0001\",\"fillPrice\":1.23456}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };
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
            UseMock: false,
            MaxOrderUnits: 100_000,
            MaxNotional: 1_000_000m,
            RequestTimeout: TimeSpan.FromSeconds(5),
            RetryInitialDelay: TimeSpan.FromSeconds(1),
            RetryMaxDelay: TimeSpan.FromSeconds(2),
            RetryMaxAttempts: 3,
            HandshakeEndpoint: "/connectivity",
            OrderEndpoint: "/orders");

        var adapter = new CTraderOpenApiExecutionAdapter(httpClient, settings);
        await adapter.ConnectAsync();
        var result = await adapter.ExecuteMarketAsync(new OrderRequest("DEC2", "EURUSD", TradeSide.Buy, 10, DateTime.UtcNow));

        Assert.True(result.Accepted);
        Assert.Equal("CT-0001", result.BrokerOrderId);
        Assert.NotNull(result.Fill);
        Assert.Equal(1.23456m, result.Fill!.Price);
    }

    [Fact]
    public void Settings_FromJson_UsesEnvironmentFallbacks()
    {
        var previousAppId = Environment.GetEnvironmentVariable("CT_APP_ID");
        var previousSecret = Environment.GetEnvironmentVariable("CT_APP_SECRET");
        var previousToken = Environment.GetEnvironmentVariable("CT_DEMO_OAUTH_TOKEN");
        var previousRefresh = Environment.GetEnvironmentVariable("CT_DEMO_REFRESH_TOKEN");
        var previousAccount = Environment.GetEnvironmentVariable("CT_DEMO_ACCOUNT_ID");
        var previousBroker = Environment.GetEnvironmentVariable("CT_DEMO_BROKER");
        try
        {
            Environment.SetEnvironmentVariable("CT_APP_ID", "env-app");
            Environment.SetEnvironmentVariable("CT_APP_SECRET", "env-secret");
            Environment.SetEnvironmentVariable("CT_DEMO_OAUTH_TOKEN", "env-token");
            Environment.SetEnvironmentVariable("CT_DEMO_REFRESH_TOKEN", "env-refresh");
            Environment.SetEnvironmentVariable("CT_DEMO_ACCOUNT_ID", "env-account");
            Environment.SetEnvironmentVariable("CT_DEMO_BROKER", "env-broker");

            var json = JsonDocument.Parse("{\"type\":\"ctrader-demo\"}").RootElement;
            var settings = CTraderAdapterSettings.FromJson(json, "ctrader-demo");

            Assert.Equal("env-app", settings.ApplicationId);
            Assert.Equal("env-secret", settings.ClientSecret);
            Assert.Equal("env-token", settings.AccessToken);
            Assert.Equal("env-refresh", settings.RefreshToken);
            Assert.Equal("env-account", settings.AccountId);
            Assert.Equal("env-broker", settings.Broker);
            Assert.False(settings.UseMock); // token fallback empty -> default mocks
        }
        finally
        {
            Environment.SetEnvironmentVariable("CT_APP_ID", previousAppId);
            Environment.SetEnvironmentVariable("CT_APP_SECRET", previousSecret);
            Environment.SetEnvironmentVariable("CT_DEMO_OAUTH_TOKEN", previousToken);
            Environment.SetEnvironmentVariable("CT_DEMO_REFRESH_TOKEN", previousRefresh);
            Environment.SetEnvironmentVariable("CT_DEMO_ACCOUNT_ID", previousAccount);
            Environment.SetEnvironmentVariable("CT_DEMO_BROKER", previousBroker);
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responders = new();

        public void Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) => _responders.Enqueue(responder);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responders.Count == 0)
            {
                throw new InvalidOperationException("No stub response available for request.");
            }

            var responder = _responders.Dequeue();
            return responder(request, cancellationToken);
        }
    }
}
