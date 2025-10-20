using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TiYf.Engine.Sim;

public sealed class CTraderOpenApiExecutionAdapter : IExecutionAdapter, IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly CTraderAdapterSettings _settings;
    private readonly Func<string, Task> _logAsync;
    private readonly Random _random = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private volatile bool _connected;
    private string _accessToken;
    private string _refreshToken;
    private bool _disposed;

    public CTraderOpenApiExecutionAdapter(HttpClient? httpClient, CTraderAdapterSettings settings, Func<string, Task>? logAsync = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClient = httpClient ?? new HttpClient();
        if (_httpClient.BaseAddress == null && settings.BaseUri.IsAbsoluteUri)
        {
            _httpClient.BaseAddress = settings.BaseUri;
        }
        _httpClient.Timeout = settings.RequestTimeout;
        _accessToken = settings.AccessToken ?? string.Empty;
        _refreshToken = settings.RefreshToken ?? string.Empty;
        _logAsync = logAsync ?? (line =>
        {
            Console.WriteLine(line);
            return Task.CompletedTask;
        });
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_settings.UseMock)
        {
            _connected = true;
            await _logAsync("Connected to cTrader endpoint (mock)");
            return;
        }

        if (_connected) return;
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connected) return;

            await ExecuteWithRetry(async token =>
            {
                await _logAsync($"cTrader handshake endpoint_raw={_settings.HandshakeEndpoint}").ConfigureAwait(false);
                var baseUri = ResolveBaseUri();
                var handshakeUri = CombineUri(baseUri, _settings.HandshakeEndpoint);
                await _logAsync($"cTrader handshake target={handshakeUri} base={baseUri} scheme={handshakeUri.Scheme} client_base={_httpClient.BaseAddress}").ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Get, handshakeUri);
                AddAuthorization(request);
                using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    _connected = true;
                    await _logAsync("Connected to cTrader endpoint").ConfigureAwait(false);
                    return;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await RefreshTokenAsync(token).ConfigureAwait(false);
                    throw new TransientCTraderException("Unauthorized during handshake.");
                }

                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                throw new PermanentCTraderException($"Handshake failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<ExecutionResult> ExecuteMarketAsync(OrderRequest order, CancellationToken ct = default)
    {
        if (order is null) throw new ArgumentNullException(nameof(order));
        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        if (Math.Abs(order.Units) > _settings.MaxOrderUnits)
        {
            return new ExecutionResult(false, $"units>{_settings.MaxOrderUnits}", null, null);
        }

        var brokerOrderId = $"CT-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Math.Abs(order.DecisionId.GetHashCode()):X}";

        if (_settings.UseMock)
        {
            return new ExecutionResult(true, string.Empty, null, brokerOrderId);
        }

        var payload = new Dictionary<string, object?>
        {
            ["accountId"] = _settings.AccountId,
            ["symbol"] = order.Symbol,
            ["side"] = order.Side == TradeSide.Buy ? "buy" : "sell",
            ["volume"] = order.Units,
            ["timeInForce"] = "IOC",
            ["clientOrderId"] = order.DecisionId,
            ["label"] = "TiYfEngine"
        };

        ExecutionFill? fill = null;
        string failureReason = string.Empty;

        try
        {
            await ExecuteWithRetry(async token =>
            {
                await _logAsync($"cTrader order endpoint_raw={_settings.OrderEndpoint}").ConfigureAwait(false);
                var baseUri = ResolveBaseUri();
                var orderUri = CombineUri(baseUri, _settings.OrderEndpoint);
                await _logAsync($"cTrader order target={orderUri} base={baseUri}").ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, orderUri);
                AddAuthorization(request);
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(body);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("orderId", out var orderId) && orderId.ValueKind == JsonValueKind.String)
                            {
                                brokerOrderId = orderId.GetString() ?? brokerOrderId;
                            }
                            else if (root.TryGetProperty("positionId", out var positionId) && positionId.ValueKind == JsonValueKind.String)
                            {
                                brokerOrderId = positionId.GetString() ?? brokerOrderId;
                            }

                            if (root.TryGetProperty("fillPrice", out var fillPrice) && fillPrice.ValueKind == JsonValueKind.Number)
                            {
                                var price = fillPrice.GetDecimal();
                                fill = new ExecutionFill(order.DecisionId, order.Symbol, order.Side, price, order.Units, DateTime.UtcNow);
                            }
                        }
                        catch (JsonException)
                        {
                            // Ignore parse failures; fallback to generated brokerOrderId
                        }
                    }
                    return;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await RefreshTokenAsync(token).ConfigureAwait(false);
                    throw new TransientCTraderException("Unauthorized during order send.");
                }

                failureReason = $"{(int)response.StatusCode} {response.ReasonPhrase}";
                var errorBody = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(errorBody))
                {
                    failureReason = $"{failureReason} {errorBody}";
                }
                throw new PermanentCTraderException($"Order send failed: {failureReason}");
            }, ct, swallowPermanentFailures: false).ConfigureAwait(false);

            return new ExecutionResult(true, string.Empty, fill, brokerOrderId);
        }
        catch (PermanentCTraderException ex)
        {
            await _logAsync($"cTrader order failure: {ex.Message}").ConfigureAwait(false);
            return new ExecutionResult(false, ex.Message, null, null);
        }
        catch (TransientCTraderException ex)
        {
            await _logAsync($"cTrader order transient failure: {ex.Message}").ConfigureAwait(false);
            return new ExecutionResult(false, ex.Message, null, null);
        }
        catch (HttpRequestException ex)
        {
            await _logAsync($"cTrader order HTTP failure: {ex.Message}").ConfigureAwait(false);
            return new ExecutionResult(false, ex.Message, null, null);
        }
        catch (TaskCanceledException ex)
        {
            await _logAsync($"cTrader order timeout: {ex.Message}").ConfigureAwait(false);
            return new ExecutionResult(false, ex.Message, null, null);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connected) return;
        await ConnectAsync(ct).ConfigureAwait(false);
    }

    private async Task ExecuteWithRetry(Func<CancellationToken, Task> operation, CancellationToken ct, bool swallowPermanentFailures = false)
    {
        var attempt = 0;
        var delay = _settings.RetryInitialDelay;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(_settings.RequestTimeout);

            try
            {
                await operation(linkedCts.Token).ConfigureAwait(false);
                return;
            }
            catch (PermanentCTraderException) when (!swallowPermanentFailures || attempt >= _settings.RetryMaxAttempts)
            {
                throw;
            }
            catch (PermanentCTraderException)
            {
                if (attempt >= _settings.RetryMaxAttempts) throw;
            }
            catch (TransientCTraderException) when (attempt < _settings.RetryMaxAttempts)
            {
                await DelayWithJitter(delay, ct).ConfigureAwait(false);
                delay = NextDelay(delay);
            }
            catch (HttpRequestException) when (attempt < _settings.RetryMaxAttempts)
            {
                await DelayWithJitter(delay, ct).ConfigureAwait(false);
                delay = NextDelay(delay);
            }
        }
    }

    private TimeSpan NextDelay(TimeSpan current)
    {
        var nextMillis = Math.Min(_settings.RetryMaxDelay.TotalMilliseconds, current.TotalMilliseconds * 2);
        return TimeSpan.FromMilliseconds(nextMillis);
    }

    private async Task DelayWithJitter(TimeSpan baseDelay, CancellationToken ct)
    {
        var jitterMs = _random.Next(250, 1000);
        var total = baseDelay + TimeSpan.FromMilliseconds(jitterMs);
        if (total < TimeSpan.Zero) total = baseDelay;
        await Task.Delay(total, ct).ConfigureAwait(false);
    }

    private void AddAuthorization(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            throw new InvalidOperationException("cTrader access token is not configured.");
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private Uri ResolveUri(string pathOrUri)
    {
        return ResolveUri(pathOrUri, ResolveBaseUri());
    }

    private Uri ResolveUri(Uri uri)
    {
        if (uri.IsAbsoluteUri) return uri;

        return ResolveUri(uri.ToString(), ResolveBaseUri());
    }

    private Uri ResolveUri(string pathOrUri, Uri baseUri)
    {
        return CombineUri(baseUri, pathOrUri);
    }

    private Uri ResolveBaseUri()
    {
        if (_httpClient.BaseAddress is { IsAbsoluteUri: true } absoluteBase)
        {
            return absoluteBase;
        }

        var candidate = _settings.BaseUri;
        if (!candidate.IsAbsoluteUri)
        {
            var text = candidate.ToString().Trim();
            if (!Uri.TryCreate(text, UriKind.Absolute, out candidate))
            {
                throw new InvalidOperationException($"BaseUri '{text}' is not absolute for cTrader adapter.");
            }
        }

        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = candidate;
        }

        return candidate;
    }

    private Uri CombineUri(Uri baseUri, string relativePath)
    {
        if (baseUri is null) throw new ArgumentNullException(nameof(baseUri));

        var candidate = string.IsNullOrWhiteSpace(relativePath) ? string.Empty : relativePath.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        var baseText = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri.AbsoluteUri
            : baseUri.AbsoluteUri + "/";
        var segment = candidate.TrimStart('/');
        var combinedText = segment.Length == 0 ? baseText : baseText + segment;

        if (!Uri.TryCreate(combinedText, UriKind.Absolute, out var combined))
        {
            throw new InvalidOperationException($"Unable to resolve URI '{relativePath}' against base '{baseUri}'.");
        }

        return combined;
    }

    private async Task RefreshTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_refreshToken))
        {
            throw new InvalidOperationException("Refresh token not configured for cTrader adapter.");
        }

        var payload = new Dictionary<string, string?>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _refreshToken,
            ["client_id"] = string.IsNullOrWhiteSpace(_settings.ClientId) ? _settings.ApplicationId : _settings.ClientId,
            ["client_secret"] = _settings.ClientSecret
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ResolveUri(_settings.TokenUri))
        {
            Content = new FormUrlEncodedContent(payload!)
        };

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Token refresh failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String)
        {
            _accessToken = at.GetString() ?? _accessToken;
        }
        if (doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String)
        {
            _refreshToken = rt.GetString() ?? _refreshToken;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connectLock.Dispose();
        if (_settings.UseMock)
        {
            return;
        }
        await Task.CompletedTask;
    }

    private sealed class TransientCTraderException : Exception
    {
        public TransientCTraderException(string message) : base(message) { }
    }

    private sealed class PermanentCTraderException : Exception
    {
        public PermanentCTraderException(string message) : base(message) { }
    }
}
