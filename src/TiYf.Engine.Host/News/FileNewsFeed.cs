using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TiYf.Engine.Core;

namespace TiYf.Engine.Host.News;

internal sealed class FileNewsFeed : INewsFeed
{
    private readonly string _path;
    private readonly ILogger _logger;

    public FileNewsFeed(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NewsEvent>> FetchAsync(DateTime? sinceUtc, int sinceOccurrencesAtTimestamp, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
        {
            return Array.Empty<NewsEvent>();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<NewsEvent>();
            }

            var events = new List<NewsEvent>();
            var cursorHits = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryReadUtc(element, out var utc))
                {
                    continue;
                }

                if (sinceUtc.HasValue)
                {
                    if (utc < sinceUtc.Value)
                    {
                        continue;
                    }

                    if (utc == sinceUtc.Value && cursorHits < sinceOccurrencesAtTimestamp)
                    {
                        cursorHits++;
                        continue;
                    }
                }

                var impact = element.TryGetProperty("impact", out var impactProp) && impactProp.ValueKind == JsonValueKind.String
                    ? impactProp.GetString() ?? string.Empty
                    : string.Empty;
                var tags = element.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array
                    ? tagsProp.EnumerateArray()
                        .Where(t => t.ValueKind == JsonValueKind.String)
                        .Select(t => t.GetString() ?? string.Empty)
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Select(t => t.Trim())
                        .ToList()
                    : new List<string>();

                events.Add(new NewsEvent(utc, impact, tags));

                if (sinceUtc.HasValue && utc == sinceUtc.Value)
                {
                    cursorHits++;
                }
            }

            return events.Count == 0
                ? Array.Empty<NewsEvent>()
                : events.OrderBy(e => e.Utc).ToList();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed reading news feed from {Path}", _path);
            return Array.Empty<NewsEvent>();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed reading news feed from {Path}", _path);
            return Array.Empty<NewsEvent>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "News feed file malformed at {Path}", _path);
            return Array.Empty<NewsEvent>();
        }
    }

    private static bool TryReadUtc(JsonElement element, out DateTime utc)
    {
        if (element.TryGetProperty("utc", out var utcProp))
        {
            if (utcProp.ValueKind == JsonValueKind.String && DateTime.TryParse(utcProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc))
            {
                if (utc.Kind != DateTimeKind.Utc)
                {
                    utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                }
                return true;
            }
        }

        utc = default;
        return false;
    }
}
