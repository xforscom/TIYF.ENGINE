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
                using var request = new HttpRequestMessage(HttpMethod.Get, EnsureAbsoluteUri(_settings.HandshakeEndpoint));
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
                using var request = new HttpRequestMessage(HttpMethod.Post, EnsureAbsoluteUri(_settings.OrderEndpoint));
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

    private Uri EnsureAbsoluteUri(string pathOrUri)
    {
        if (Uri.TryCreate(pathOrUri, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        if (_httpClient.BaseAddress != null)
        {
            return new Uri(_httpClient.BaseAddress, pathOrUri);
        }

        return new Uri(pathOrUri, UriKind.RelativeOrAbsolute);
    }

    private Uri EnsureAbsoluteUri(Uri uri)
    {
        if (uri.IsAbsoluteUri) return uri;
        if (_httpClient.BaseAddress != null)
        {
            return new Uri(_httpClient.BaseAddress, uri.ToString());
        }
        return uri;
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

        using var request = new HttpRequestMessage(HttpMethod.Post, EnsureAbsoluteUri(_settings.TokenUri))
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
