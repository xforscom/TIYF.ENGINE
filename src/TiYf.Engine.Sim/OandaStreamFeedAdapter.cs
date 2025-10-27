using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
namespace TiYf.Engine.Sim;

public readonly record struct OandaStreamEvent(bool IsHeartbeat, string? Instrument, decimal Bid, decimal Ask, DateTime Timestamp);

public sealed class OandaStreamFeedAdapter : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly OandaAdapterSettings _settings;
    private readonly OandaStreamSettings _streamSettings;
    private readonly Action<string>? _logWarning;
    private readonly Action<Exception, string>? _logError;
    private bool _disposed;

    public OandaStreamFeedAdapter(
        HttpClient httpClient,
        OandaAdapterSettings settings,
        OandaStreamSettings streamSettings,
        Action<string>? logWarning = null,
        Action<Exception, string>? logError = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _streamSettings = streamSettings ?? throw new ArgumentNullException(nameof(streamSettings));
        _logWarning = logWarning;
        _logError = logError;
    }

    public async Task RunAsync(Func<OandaStreamEvent, Task> onEvent, Func<Task>? onConnected, Func<Task>? onDisconnected, CancellationToken ct)
    {
        if (onEvent is null) throw new ArgumentNullException(nameof(onEvent));

        var requestUri = BuildRequestUri();
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                AddAuthorization(request);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                if (onConnected is not null)
                {
                    await onConnected().ConfigureAwait(false);
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (TryParse(line, out var evt))
                    {
                        await onEvent(evt).ConfigureAwait(false);
                    }
                }

                attempt = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                _logError?.Invoke(ex, $"OANDA stream failure attempt={attempt}");

                if (onDisconnected is not null)
                {
                    await onDisconnected().ConfigureAwait(false);
                }

                var delay = CalculateDelay(attempt);
                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
    }

    internal bool TryParse(string payload, out OandaStreamEvent evt)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                evt = default;
                return false;
            }

            var type = typeEl.GetString();
            if (string.Equals(type, "HEARTBEAT", StringComparison.OrdinalIgnoreCase))
            {
                var ts = ParseTimestamp(root.TryGetProperty("time", out var timeEl) ? timeEl.GetString() : null);
                evt = new OandaStreamEvent(true, null, 0m, 0m, ts);
                return true;
            }

            if (!string.Equals(type, "PRICE", StringComparison.OrdinalIgnoreCase))
            {
                evt = default;
                return false;
            }

            var instrument = root.TryGetProperty("instrument", out var instEl) && instEl.ValueKind == JsonValueKind.String
                ? instEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(instrument))
            {
                evt = default;
                return false;
            }

            decimal bid = 0m;
            decimal ask = 0m;
            if (root.TryGetProperty("bids", out var bidsEl) && bidsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var bidEntry in bidsEl.EnumerateArray())
                {
                    if (bidEntry.TryGetProperty("price", out var priceEl) && priceEl.ValueKind == JsonValueKind.String &&
                        decimal.TryParse(priceEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        bid = parsed;
                        break;
                    }
                }
            }

            if (root.TryGetProperty("asks", out var asksEl) && asksEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var askEntry in asksEl.EnumerateArray())
                {
                    if (askEntry.TryGetProperty("price", out var priceEl) && priceEl.ValueKind == JsonValueKind.String &&
                        decimal.TryParse(priceEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        ask = parsed;
                        break;
                    }
                }
            }

            var timestamp = ParseTimestamp(root.TryGetProperty("time", out var priceTimeEl) ? priceTimeEl.GetString() : null);
            evt = new OandaStreamEvent(false, instrument, bid, ask, timestamp);
            return true;
        }
        catch (JsonException ex)
        {
            _logWarning?.Invoke($"Failed to parse OANDA stream payload: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logWarning?.Invoke($"Unexpected error parsing OANDA stream payload: {ex.Message}");
        }

        evt = default;
        return false;
    }

    private Uri BuildRequestUri()
    {
        var baseUri = EnsureAbsoluteUri(_streamSettings.BaseUri);
        var endpoint = ExpandEndpoint(_streamSettings.PricingEndpoint);
        var builder = new UriBuilder(baseUri);
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }
        builder.Path = string.Concat(builder.Path, endpoint.TrimStart('/'));
        var instrumentsParam = string.Join(',', _streamSettings.Instruments);
        builder.Query = $"instruments={Uri.EscapeDataString(instrumentsParam)}";
        return builder.Uri;
    }

    private void AddAuthorization(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_settings.AccessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AccessToken);
        }
    }

    internal TimeSpan CalculateDelay(int attempt)
    {
        var capSeconds = Math.Max(1d, _streamSettings.MaxBackoff.TotalSeconds);
        var seconds = Math.Min(capSeconds, Math.Pow(2, attempt));
        return TimeSpan.FromSeconds(seconds);
    }

    private Uri EnsureAbsoluteUri(Uri candidate)
    {
        if (candidate.IsAbsoluteUri) return candidate;
        var text = candidate.ToString();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var absolute))
        {
            throw new InvalidOperationException($"Stream base URI '{text}' is not absolute");
        }
        return absolute;
    }

    private string ExpandEndpoint(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "/accounts/{accountId}/pricing/stream";
        return template.Replace("{accountId}", _settings.AccountId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTime.UtcNow;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ts))
        {
            return DateTime.SpecifyKind(ts, DateTimeKind.Utc);
        }
        return DateTime.UtcNow;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
