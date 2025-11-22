using Microsoft.Extensions.Options;

namespace OpsDashboard.Services;

public sealed class MetricsClient
{
    private readonly IHttpClientFactory _factory;
    private readonly DashboardOptions _options;

    public MetricsClient(IHttpClientFactory factory, IOptions<DashboardOptions> options)
    {
        _factory = factory;
        _options = options.Value;
    }

    public async Task<MetricsResult> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = _options.EngineBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return MetricsResult.MissingBaseUrl;
        }

        try
        {
            var client = _factory.CreateClient("engine");
            client.BaseAddress = new Uri(baseUrl);
            using var response = await client.GetAsync("/metrics", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return MetricsResult.FromError($"HTTP {(int)response.StatusCode}");
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text)
                ? MetricsResult.FromError("Empty /metrics payload")
                : MetricsResult.Success(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return MetricsResult.FromError(ex.Message);
        }
    }
}

public sealed class MetricsResult
{
    private MetricsResult(bool success, string? text, string? error)
    {
        Success = success;
        Text = text;
        Error = error;
    }

    public bool Success { get; }
    public string? Text { get; }
    public string? Error { get; }

    public static MetricsResult Success(string text) => new(true, text, null);

    public static MetricsResult FromError(string message) => new(false, null, message);

    public static MetricsResult MissingBaseUrl => FromError("DASHBOARD_ENGINE_BASE_URL not configured");
}
