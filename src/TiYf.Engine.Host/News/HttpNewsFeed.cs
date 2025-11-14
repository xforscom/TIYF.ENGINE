using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TiYf.Engine.Core;

namespace TiYf.Engine.Host.News;

internal sealed class HttpNewsFeed : INewsFeed
{
    private readonly HttpClient _client;
    private readonly ILogger _logger;
    private readonly Uri _baseUri;
    private readonly string? _apiKeyHeaderName;
    private readonly string? _apiKeyValue;
    private readonly IReadOnlyDictionary<string, string> _staticHeaders;
    private readonly IReadOnlyDictionary<string, string> _queryParameters;

    public HttpNewsFeed(
        HttpClient client,
        ILogger logger,
        Uri baseUri,
        string? apiKeyHeaderName,
        string? apiKeyValue,
        IReadOnlyDictionary<string, string>? headers,
        IReadOnlyDictionary<string, string>? queryParameters)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        _apiKeyHeaderName = string.IsNullOrWhiteSpace(apiKeyHeaderName) ? null : apiKeyHeaderName;
        _apiKeyValue = string.IsNullOrWhiteSpace(apiKeyValue) ? null : apiKeyValue;
        _staticHeaders = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _queryParameters = queryParameters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<NewsEvent>> FetchAsync(DateTime? sinceUtc, int sinceOccurrencesAtTimestamp, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(sinceUtc));
            foreach (var header in _staticHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (!string.IsNullOrWhiteSpace(_apiKeyHeaderName) && !string.IsNullOrWhiteSpace(_apiKeyValue))
            {
                request.Headers.TryAddWithoutValidation(_apiKeyHeaderName!, _apiKeyValue!);
            }

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("News HTTP feed returned status {StatusCode}", (int)response.StatusCode);
                return Array.Empty<NewsEvent>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await ParseEventsAsync(stream, sinceUtc, sinceOccurrencesAtTimestamp, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error fetching news feed from {Uri}", _baseUri);
            return Array.Empty<NewsEvent>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed news feed payload from {Uri}", _baseUri);
            return Array.Empty<NewsEvent>();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "I/O error fetching news feed from {Uri}", _baseUri);
            return Array.Empty<NewsEvent>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed fetching news feed from {Uri}", _baseUri);
            return Array.Empty<NewsEvent>();
        }
    }

    private Uri BuildRequestUri(DateTime? sinceUtc)
    {
        var baseText = _baseUri.ToString();
        var builder = new StringBuilder(baseText);
        var hasQuery = baseText.Contains('?', StringComparison.Ordinal);
        foreach (var kvp in _queryParameters)
        {
            AppendQuery(builder, kvp.Key, kvp.Value, ref hasQuery);
        }
        if (sinceUtc.HasValue)
        {
            AppendQuery(builder, "since_utc", sinceUtc.Value.ToString("O", CultureInfo.InvariantCulture), ref hasQuery);
        }
        return new Uri(builder.ToString(), UriKind.Absolute);
    }

    private static void AppendQuery(StringBuilder builder, string key, string value, ref bool hasQuery)
    {
        builder.Append(hasQuery ? '&' : '?');
        hasQuery = true;
        builder.Append(Uri.EscapeDataString(key ?? string.Empty));
        builder.Append('=');
        builder.Append(Uri.EscapeDataString(value ?? string.Empty));
    }

    private static async Task<IReadOnlyList<NewsEvent>> ParseEventsAsync(Stream stream, DateTime? sinceUtc, int sinceOccurrencesAtTimestamp, CancellationToken token)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
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

    private static bool TryReadUtc(JsonElement element, out DateTime utc)
    {
        if (element.TryGetProperty("utc", out var utcProp) &&
            utcProp.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(utcProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc))
        {
            if (utc.Kind != DateTimeKind.Utc)
            {
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            }
            return true;
        }

        utc = default;
        return false;
    }
}
