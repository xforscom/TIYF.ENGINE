using System;
using System.Collections.Generic;
using System.IO;
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
            var all = await feed.FetchAsync(null, CancellationToken.None);
            Assert.Equal(3, all.Count);

            var filtered = await feed.FetchAsync(all[1].Utc, CancellationToken.None);
            Assert.Single(filtered);
            Assert.Equal(all[2].Utc, filtered[0].Utc);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
