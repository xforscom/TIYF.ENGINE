using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TiYf.Engine.Sim;

public sealed class OandaRestExecutionAdapter : IConnectableExecutionAdapter, IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly OandaAdapterSettings _settings;
    private readonly Func<string, Task> _logAsync;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly Random _random = new();
    private volatile bool _connected;
    private bool _disposed;

    public OandaRestExecutionAdapter(HttpClient? httpClient, OandaAdapterSettings settings, Func<string, Task>? logAsync = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClient = httpClient ?? new HttpClient();
        if (_httpClient.BaseAddress == null && settings.BaseUri.IsAbsoluteUri)
        {
            _httpClient.BaseAddress = settings.BaseUri;
        }
        _httpClient.Timeout = settings.RequestTimeout;
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
            await _logAsync("Connected to OANDA endpoint (mock)").ConfigureAwait(false);
            return;
        }

        if (_connected) return;
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connected) return;

            await ExecuteWithRetry(async token =>
            {
                await _logAsync($"OANDA handshake endpoint_raw={_settings.HandshakeEndpoint}").ConfigureAwait(false);
                var baseUri = ResolveBaseUri();
                var handshakeUri = CombineUri(baseUri, ExpandEndpoint(_settings.HandshakeEndpoint));
                await _logAsync($"OANDA handshake target={handshakeUri} base={baseUri} scheme={handshakeUri.Scheme} client_base={_httpClient.BaseAddress}").ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Get, handshakeUri);
                AddAuthorization(request);
                using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    _connected = true;
                    var modeLabel = string.Equals(_settings.Mode, "oanda-demo", StringComparison.OrdinalIgnoreCase) ? "practice" : "live";
                    await _logAsync($"Connected to OANDA ({modeLabel})").ConfigureAwait(false);
                    return true;
                }

                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new TransientOandaException($"Unauthorized during handshake. {body}");
                }

                throw new PermanentOandaException($"Handshake failed: {(int)response.StatusCode} {response.ReasonPhrase} {body}");
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

        if (Math.Abs(order.Units) > _settings.MaxOrderUnits)
        {
            return new ExecutionResult(false, $"units>{_settings.MaxOrderUnits}", null, null);
        }

        var brokerOrderId = $"OANDA-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Math.Abs(order.DecisionId.GetHashCode()):X}";

        if (_settings.UseMock)
        {
            return new ExecutionResult(true, string.Empty, null, brokerOrderId);
        }

        await EnsureConnectedAsync(ct).ConfigureAwait(false);

        try
        {
            var result = await ExecuteWithRetry(async token =>
            {
                await _logAsync($"OANDA order endpoint_raw={_settings.OrderEndpoint}").ConfigureAwait(false);
                var baseUri = ResolveBaseUri();
                var orderUri = CombineUri(baseUri, ExpandEndpoint(_settings.OrderEndpoint));
                await _logAsync($"OANDA order target={orderUri} base={baseUri}").ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, orderUri);
                AddAuthorization(request);
                request.Content = BuildOrderContent(order);
                using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var (fill, oandaOrderId) = TryParseOrderSuccess(order, body);
                    var finalOrderId = string.IsNullOrWhiteSpace(oandaOrderId) ? brokerOrderId : oandaOrderId!;
                    return new ExecutionResult(true, string.Empty, fill, finalOrderId);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new TransientOandaException("Unauthorized during order send.");
                }

                var reason = ExtractErrorReason(body);
                throw new PermanentOandaException($"Order send failed: {(int)response.StatusCode} {response.ReasonPhrase} {reason}");
            }, ct).ConfigureAwait(false);

            return result;
        }
        catch (PermanentOandaException ex)
        {
            await _logAsync($"OANDA order failure: {ex.Message}").ConfigureAwait(false);
            return new ExecutionResult(false, ex.Message, null, null);
        }
        catch (TransientOandaException ex)
        {
            await _logAsync($"OANDA order transient failure: {ex.Message}").ConfigureAwait(false);
            return new ExecutionResult(false, ex.Message, null, null);
        }
        catch (HttpRequestException ex)
        {
            await _logAsync($"OANDA order HTTP failure: {ex.Message}").ConfigureAwait(false);
            return new ExecutionResult(false, ex.Message, null, null);
        }
        catch (TaskCanceledException ex)
        {
            await _logAsync($"OANDA order timeout: {ex.Message}").ConfigureAwait(false);
            return new ExecutionResult(false, ex.Message, null, null);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connected) return;
        await ConnectAsync(ct).ConfigureAwait(false);
    }

    private StringContent BuildOrderContent(OrderRequest order)
    {
        var instrument = MapInstrument(order.Symbol);
        var signedUnits = order.Side == TradeSide.Buy ? Math.Abs(order.Units) : -Math.Abs(order.Units);
        var payload = new
        {
            order = new
            {
                units = signedUnits.ToString(CultureInfo.InvariantCulture),
                instrument,
                timeInForce = "FOK",
                type = "MARKET",
                positionFill = "DEFAULT",
                clientExtensions = new
                {
                    id = order.DecisionId,
                    tag = "TiYfEngine",
                    comment = "TiYf Engine market order"
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private (ExecutionFill? Fill, string? OrderId) TryParseOrderSuccess(OrderRequest order, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string? orderId = null;
            if (root.TryGetProperty("lastTransactionID", out var lastTxn) && lastTxn.ValueKind == JsonValueKind.String)
            {
                orderId = lastTxn.GetString();
            }

            decimal? price = null;
            if (root.TryGetProperty("orderFillTransaction", out var fillTx) && fillTx.ValueKind == JsonValueKind.Object)
            {
                if (fillTx.TryGetProperty("price", out var priceNode))
                {
                    if (priceNode.ValueKind == JsonValueKind.Number)
                    {
                        price = priceNode.GetDecimal();
                    }
                    else if (priceNode.ValueKind == JsonValueKind.String &&
                             decimal.TryParse(priceNode.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        price = parsed;
                    }
                }
                else if (fillTx.TryGetProperty("fullPrice", out var fullPriceNode) && fullPriceNode.ValueKind == JsonValueKind.Object)
                {
                    if (fullPriceNode.TryGetProperty("closeoutAsk", out var askNode) &&
                        askNode.ValueKind == JsonValueKind.String &&
                        decimal.TryParse(askNode.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ask))
                    {
                        price = ask;
                    }
                    else if (fullPriceNode.TryGetProperty("closeoutBid", out var bidNode) &&
                             bidNode.ValueKind == JsonValueKind.String &&
                             decimal.TryParse(bidNode.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bid))
                    {
                        price = bid;
                    }
                }

                if (string.IsNullOrWhiteSpace(orderId) && fillTx.TryGetProperty("orderID", out var orderIdNode) && orderIdNode.ValueKind == JsonValueKind.String)
                {
                    orderId = orderIdNode.GetString();
                }
            }

            if (price.HasValue)
            {
                var fill = new ExecutionFill(order.DecisionId, order.Symbol, order.Side, price.Value, Math.Abs(order.Units), DateTime.UtcNow);
                return (fill, orderId);
            }

            return (null, orderId);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private string ExtractErrorReason(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("errorMessage", out var messageNode) && messageNode.ValueKind == JsonValueKind.String)
            {
                return messageNode.GetString() ?? string.Empty;
            }
            if (root.TryGetProperty("errorCode", out var codeNode))
            {
                return $"{codeNode}";
            }
        }
        catch (JsonException)
        {
            // fall back to raw body
        }
        return body;
    }

    private void AddAuthorization(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_settings.AccessToken))
        {
            throw new InvalidOperationException("OANDA access token is not configured.");
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AccessToken);
        if (!request.Headers.Accept.Any(h => h.MediaType == "application/json"))
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
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
                throw new InvalidOperationException($"BaseUri '{text}' is not absolute for OANDA adapter.");
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
        _ = _logAsync($"OANDA CombineUri base={baseUri} candidate_raw={relativePath} trimmed={candidate}");
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return absolute;
        }

        var baseAbsolute = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/", UriKind.Absolute);

        var segment = candidate.TrimStart('/');
        _ = _logAsync($"OANDA CombineUri segment={segment}");
        var result = segment.Length == 0
            ? baseAbsolute
            : new Uri(baseAbsolute, segment);
        _ = _logAsync($"OANDA CombineUri result={result} scheme={result.Scheme}");
        return result;
    }

    private string ExpandEndpoint(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return template;
        return template.Replace("{accountId}", _settings.AccountId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private string MapInstrument(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return symbol;
        if (symbol.Contains('_')) return symbol.ToUpperInvariant();
        if (symbol.Length == 6)
        {
            return $"{symbol[..3].ToUpperInvariant()}_{symbol[3..].ToUpperInvariant()}";
        }
        return symbol.ToUpperInvariant();
    }

    private async Task<T> ExecuteWithRetry<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct, bool swallowPermanentFailures = false)
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
                return await operation(linkedCts.Token).ConfigureAwait(false);
            }
            catch (TransientOandaException)
            {
                if (attempt >= _settings.RetryMaxAttempts)
                {
                    throw;
                }
                await DelayWithJitter(delay, ct).ConfigureAwait(false);
                delay = NextDelay(delay);
            }
            catch (PermanentOandaException)
            {
                throw;
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
        var nextMillis = Math.Min(_settings.RetryMaxDelay.TotalMilliseconds, Math.Max(current.TotalMilliseconds * 2, 1));
        return TimeSpan.FromMilliseconds(nextMillis);
    }

    private async Task DelayWithJitter(TimeSpan baseDelay, CancellationToken ct)
    {
        var jitterMs = _random.Next(150, 750);
        var total = baseDelay + TimeSpan.FromMilliseconds(jitterMs);
        if (total < TimeSpan.Zero) total = baseDelay;
        await Task.Delay(total, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connectLock.Dispose();
        await Task.CompletedTask;
    }

    private sealed class TransientOandaException : Exception
    {
        public TransientOandaException(string message) : base(message) { }
    }

    private sealed class PermanentOandaException : Exception
    {
        public PermanentOandaException(string message) : base(message) { }
    }
}
