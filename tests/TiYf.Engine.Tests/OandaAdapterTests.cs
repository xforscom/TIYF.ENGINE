using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TiYf.Engine.Sim;
using Xunit;

namespace TiYf.Engine.Tests;

public class OandaAdapterTests
{
    [Fact]
    public async Task ConnectAsync_WithMock_Succeeds()
    {
        var settings = new OandaAdapterSettings(
            Mode: "oanda-demo",
            BaseUri: new Uri("https://example.com/v3/"),
            AccountId: "practice-account",
            AccessToken: "token",
            UseMock: true,
            MaxOrderUnits: 100_000,
            BrokerDailyLossCapCcy: null,
            BrokerMaxUnits: null,
            BrokerSymbolUnitCaps: null,
            RequestTimeout: TimeSpan.FromSeconds(5),
            RetryInitialDelay: TimeSpan.FromMilliseconds(5),
            RetryMaxDelay: TimeSpan.FromMilliseconds(10),
            RetryMaxAttempts: 2,
            HandshakeEndpoint: "/accounts/{accountId}/summary",
            OrderEndpoint: "/accounts/{accountId}/orders",
            PositionsEndpoint: "/accounts/{accountId}/openPositions",
            PendingOrdersEndpoint: "/accounts/{accountId}/orders?state=PENDING");

        var adapter = new OandaRestExecutionAdapter(new HttpClient(), settings);
        await adapter.ConnectAsync();
        var result = await adapter.ExecuteMarketAsync(new OrderRequest("DEC-1", "EURUSD", TradeSide.Buy, 1_000, DateTime.UtcNow));

        Assert.True(result.Accepted);
        Assert.NotNull(result.BrokerOrderId);
    }

    [Fact]
    public async Task ExecuteMarketAsync_ParsesFill()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue((req, _) =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal("https://example.com/v3/accounts/practice-account/summary", req.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        handler.Enqueue(async (req, token) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("https://example.com/v3/accounts/practice-account/orders", req.RequestUri!.ToString());
            Assert.NotNull(req.Content);
            var payload = await req.Content!.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(payload);
            var order = doc.RootElement.GetProperty("order");
            Assert.Equal("EUR_USD", order.GetProperty("instrument").GetString());
            Assert.Equal("-5000", order.GetProperty("units").GetString());

            var responseBody = """
            {
                "lastTransactionID": "42",
                "orderFillTransaction": {
                    "orderID": "42",
                    "price": "1.10123"
                }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com/v3/") };
        var settings = new OandaAdapterSettings(
            Mode: "oanda-demo",
            BaseUri: new Uri("https://example.com/v3/"),
            AccountId: "practice-account",
            AccessToken: "token",
            UseMock: false,
            MaxOrderUnits: 100_000,
            BrokerDailyLossCapCcy: null,
            BrokerMaxUnits: null,
            BrokerSymbolUnitCaps: null,
            RequestTimeout: TimeSpan.FromSeconds(5),
            RetryInitialDelay: TimeSpan.FromMilliseconds(5),
            RetryMaxDelay: TimeSpan.FromMilliseconds(20),
            RetryMaxAttempts: 3,
            HandshakeEndpoint: "/accounts/{accountId}/summary",
            OrderEndpoint: "/accounts/{accountId}/orders",
            PositionsEndpoint: "/accounts/{accountId}/openPositions",
            PendingOrdersEndpoint: "/accounts/{accountId}/orders?state=PENDING");

        var adapter = new OandaRestExecutionAdapter(httpClient, settings);
        await adapter.ConnectAsync();
        var result = await adapter.ExecuteMarketAsync(new OrderRequest("DEC-2", "EURUSD", TradeSide.Sell, 5_000, DateTime.UtcNow));

        Assert.True(result.Accepted);
        Assert.Equal("42", result.BrokerOrderId);
        Assert.NotNull(result.Fill);
        Assert.Equal(1.10123m, result.Fill!.Price);
        Assert.Equal(5_000, result.Fill!.Units);
    }

    [Fact]
    public void Settings_FromJson_UsesEnvironmentFallbacks()
    {
        var prevToken = Environment.GetEnvironmentVariable("OANDA_PRACTICE_TOKEN");
        var prevAccount = Environment.GetEnvironmentVariable("OANDA_PRACTICE_ACCOUNT_ID");
        try
        {
            Environment.SetEnvironmentVariable("OANDA_PRACTICE_TOKEN", "env-token");
            Environment.SetEnvironmentVariable("OANDA_PRACTICE_ACCOUNT_ID", "env-account");

            using var doc = JsonDocument.Parse("{\"type\":\"oanda-demo\"}");
            var settings = OandaAdapterSettings.FromJson(doc.RootElement, "oanda-demo");

            Assert.Equal("env-token", settings.AccessToken);
            Assert.Equal("env-account", settings.AccountId);
            Assert.False(settings.UseMock);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OANDA_PRACTICE_TOKEN", prevToken);
            Environment.SetEnvironmentVariable("OANDA_PRACTICE_ACCOUNT_ID", prevAccount);
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
