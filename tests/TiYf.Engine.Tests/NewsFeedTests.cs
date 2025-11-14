using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TiYf.Engine.Core;
using TiYf.Engine.Host.News;
using Xunit;

namespace TiYf.Engine.Tests;

public class NewsFeedTests
{
    [Fact]
    public async Task FileNewsFeed_FiltersByLastSeen()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var events = new[]
            {
                new { utc = "2025-01-01T10:00:00Z", impact = "high", tags = new[] { "USD" } },
                new { utc = "2025-01-01T11:00:00Z", impact = "medium", tags = new[] { "EUR" } },
                new { utc = "2025-01-01T12:00:00Z", impact = "low", tags = new[] { "JPY" } }
            };
            File.WriteAllText(tempFile, JsonSerializer.Serialize(events));

            var feed = new FileNewsFeed(tempFile, NullLogger.Instance);
            var all = await feed.FetchAsync(null, 0, CancellationToken.None);
            Assert.Equal(3, all.Count);

            var filtered = await feed.FetchAsync(all[1].Utc, 1, CancellationToken.None);
            Assert.Single(filtered);
            Assert.Equal(all[2].Utc, filtered[0].Utc);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task FileNewsFeed_RetainsEventsWithSameTimestamp()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var events = new[]
            {
                new { utc = "2025-02-01T09:00:00Z", impact = "high", tags = new[] { "USD" } },
                new { utc = "2025-02-01T09:00:00Z", impact = "medium", tags = new[] { "EUR" } },
                new { utc = "2025-02-01T10:00:00Z", impact = "low", tags = new[] { "JPY" } }
            };
            File.WriteAllText(tempFile, JsonSerializer.Serialize(events));

            var feed = new FileNewsFeed(tempFile, NullLogger.Instance);
            var initial = await feed.FetchAsync(null, 0, CancellationToken.None);
            Assert.Equal(3, initial.Count);

            // simulate cursor at first timestamp with both occurrences consumed
            var updated = await feed.FetchAsync(initial[0].Utc, 2, CancellationToken.None);
            Assert.Single(updated);
            Assert.Equal(initial[2].Utc, updated[0].Utc);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task HttpNewsFeed_UsesCursorAndHeaders()
    {
        var payloads = new Queue<string>();
        payloads.Enqueue(JsonSerializer.Serialize(new[]
        {
            new { utc = "2025-03-01T09:00:00Z", impact = "high", tags = new[] { "USD" } },
            new { utc = "2025-03-01T09:30:00Z", impact = "medium", tags = new[] { "EUR" } }
        }));
        payloads.Enqueue(JsonSerializer.Serialize(new[]
        {
            new { utc = "2025-03-01T09:30:00Z", impact = "medium", tags = new[] { "EUR" } },
            new { utc = "2025-03-01T10:00:00Z", impact = "low", tags = new[] { "JPY" } }
        }));

        var requests = new List<Uri>();
        var handler = new StubHandler(req =>
        {
            requests.Add(req.RequestUri!);
            var json = payloads.Count > 0 ? payloads.Dequeue() : "[]";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        });
        var client = new HttpClient(handler);
        var feed = new HttpNewsFeed(client, NullLogger.Instance, new Uri("https://example.test/news"), "X-API-KEY", "secret", new Dictionary<string, string> { ["Accept"] = "application/json" }, null);

        var firstBatch = await feed.FetchAsync(null, 0, CancellationToken.None);
        Assert.Equal(2, firstBatch.Count);
        Assert.Equal("https://example.test/news", requests[0].ToString());

        var secondBatch = await feed.FetchAsync(firstBatch[^1].Utc, 1, CancellationToken.None);
        Assert.Single(secondBatch);
        Assert.Contains("since_utc", requests.Last().Query);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
