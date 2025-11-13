using System;
using System.Net.Http;
using System.Threading.Tasks;
using TiYf.Engine.Sim;
using Xunit;

namespace TiYf.Engine.Tests;

public class OandaStreamFeedAdapterTests
{
    private static OandaAdapterSettings CreateAdapterSettings() =>
        new(
            Mode: "oanda-demo",
            BaseUri: new Uri("https://api-fxpractice.oanda.com/v3/"),
            AccountId: "ACCOUNT",
            AccessToken: "TOKEN",
            UseMock: false,
            MaxOrderUnits: 100_000,
            RequestTimeout: TimeSpan.FromSeconds(5),
            RetryInitialDelay: TimeSpan.FromMilliseconds(100),
            RetryMaxDelay: TimeSpan.FromSeconds(2),
            RetryMaxAttempts: 3,
            HandshakeEndpoint: "/accounts/{accountId}/summary",
            OrderEndpoint: "/accounts/{accountId}/orders",
            PositionsEndpoint: "/accounts/{accountId}/openPositions",
            PendingOrdersEndpoint: "/accounts/{accountId}/orders?state=PENDING");

    private static OandaStreamSettings CreateStreamSettings(TimeSpan? maxBackoff = null) =>
        new(
            Enable: true,
            BaseUri: new Uri("https://stream-fxpractice.oanda.com/v3/"),
            PricingEndpoint: "/accounts/{accountId}/pricing/stream",
            Instruments: new[] { "EUR_USD" },
            HeartbeatTimeout: TimeSpan.FromSeconds(15),
            MaxBackoff: maxBackoff ?? TimeSpan.FromSeconds(5),
            FeedMode: "live",
            ReplayTicksFile: null);

    [Fact]
    public async Task TryParse_HeartbeatPayload_ReturnsHeartbeatEvent()
    {
        await using var adapter = new OandaStreamFeedAdapter(
            new HttpClient(),
            CreateAdapterSettings(),
            CreateStreamSettings());

        const string payload = "{\"type\":\"HEARTBEAT\",\"time\":\"2024-01-01T00:00:00Z\"}";

        var success = adapter.TryParse(payload, out var evt);

        Assert.True(success);
        Assert.True(evt.IsHeartbeat);
        Assert.Equal(DateTimeKind.Utc, evt.Timestamp.Kind);
    }

    [Fact]
    public async Task TryParse_PricePayload_EmitsInstrumentAndPrices()
    {
        await using var adapter = new OandaStreamFeedAdapter(
            new HttpClient(),
            CreateAdapterSettings(),
            CreateStreamSettings());

        const string payload = """
            {
              "type":"PRICE",
              "time":"2024-01-01T01:02:03Z",
              "instrument":"EUR_USD",
              "bids":[{"price":"1.2345"}],
              "asks":[{"price":"1.2347"}]
            }
            """;

        var success = adapter.TryParse(payload, out var evt);

        Assert.True(success);
        Assert.False(evt.IsHeartbeat);
        Assert.Equal("EUR_USD", evt.Instrument);
        Assert.Equal(1.2345m, evt.Bid);
        Assert.Equal(1.2347m, evt.Ask);
        Assert.Equal(DateTimeKind.Utc, evt.Timestamp.Kind);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 5)] // capped at max backoff seconds
    public async Task CalculateDelay_UsesExponentialBackoffWithCap(int attempt, double expectedSeconds)
    {
        var streamSettings = CreateStreamSettings(TimeSpan.FromSeconds(5));
        await using var adapter = new OandaStreamFeedAdapter(
            new HttpClient(),
            CreateAdapterSettings(),
            streamSettings);

        var delay = adapter.CalculateDelay(attempt);

        Assert.Equal(expectedSeconds, Math.Round(delay.TotalSeconds, 6));
        Assert.True(delay <= streamSettings.MaxBackoff);
    }

    [Fact]
    public async Task TryParse_InvalidTimestamp_UsesSentinel()
    {
        var streamSettings = CreateStreamSettings();
        await using var adapter = new OandaStreamFeedAdapter(
            new HttpClient(),
            CreateAdapterSettings(),
            streamSettings);

        var payload = """
        {
          "type": "HEARTBEAT",
          "time": ""
        }
        """;

        var parsed = adapter.TryParse(payload, out var evt);

        Assert.True(parsed);
        Assert.True(evt.IsHeartbeat);
        Assert.True(OandaStreamFeedAdapter.IsTimestampSentinel(evt.Timestamp));
        Assert.Equal(DateTimeKind.Utc, evt.Timestamp.Kind);
    }
}
