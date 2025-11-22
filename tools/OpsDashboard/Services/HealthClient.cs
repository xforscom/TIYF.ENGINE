using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OpsDashboard.Services;

public sealed class HealthClient
{
    private readonly IHttpClientFactory _factory;
    private readonly DashboardOptions _options;

    public HealthClient(IHttpClientFactory factory, IOptions<DashboardOptions> options)
    {
        _factory = factory;
        _options = options.Value;
    }

    public async Task<HealthResult> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = _options.EngineBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return HealthResult.MissingBaseUrl;
        }

        try
        {
            var client = _factory.CreateClient("engine");
            client.BaseAddress = new Uri(baseUrl);
            using var response = await client.GetAsync("/health", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return HealthResult.FromError($"HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return HealthResult.FromError("Empty /health payload");
            }

            var parsed = JsonDocument.Parse(json);
            return HealthResult.SuccessResult(parsed, json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return HealthResult.FromError(ex.Message);
        }
    }
}

public sealed class HealthResult : IDisposable
{
    private readonly JsonDocument? _document;

    private HealthResult(bool success, JsonDocument? document, string? raw, string? error)
    {
        Success = success;
        _document = document;
        Raw = raw;
        Error = error;
        Document = document;
    }

    public bool Success { get; }
    public JsonDocument? Document { get; }
    public string? Raw { get; }
    public string? Error { get; }

    public static HealthResult SuccessResult(JsonDocument document, string raw) =>
        new(true, document, raw, null);

    public static HealthResult FromError(string message) =>
        new(false, null, null, message);

    public static HealthResult MissingBaseUrl =>
        new(false, null, null, "DASHBOARD_ENGINE_BASE_URL not configured");

    public void Dispose()
    {
        _document?.Dispose();
    }
}
