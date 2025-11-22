using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpsDashboard.Services;
using Xunit;

namespace OpsDashboard.Tests;

public class HealthClientTests
{
    [Fact]
    public async Task ParsesHealthPayload()
    {
        var json = """{"adapter":"oanda-demo","config":{"id":"demo-oanda-v1"}}""";
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });
        var factory = new StubHttpClientFactory(handler);
        var client = new HealthClient(factory, Options.Create(new DashboardOptions { EngineBaseUrl = "http://localhost:5000" }));

        var result = await client.GetHealthAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Document);
        Assert.Equal("oanda-demo", result.Document!.RootElement.GetProperty("adapter").GetString());
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new HttpClient(_handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }
}
