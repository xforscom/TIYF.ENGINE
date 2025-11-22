using System.Text.Json;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using OpsDashboard.Services;

namespace OpsDashboard.Pages;

public sealed class IndexModel : PageModel
{
    private readonly HealthClient _healthClient;
    private readonly MetricsClient _metricsClient;
    private readonly DashboardOptions _options;

    public IndexModel(HealthClient healthClient, MetricsClient metricsClient, IOptions<DashboardOptions> options)
    {
        _healthClient = healthClient;
        _metricsClient = metricsClient;
        _options = options.Value;
    }

    public string EngineBaseUrlDisplay => string.IsNullOrWhiteSpace(_options.EngineBaseUrl)
        ? "(not configured)"
        : _options.EngineBaseUrl!;

    public string? Warning { get; private set; }
    public string? HealthJson { get; private set; }
    public string? HealthError { get; private set; }
    public string? MetricsRaw { get; private set; }
    public string? MetricsError { get; private set; }

    public async Task OnGetAsync()
    {
        if (string.IsNullOrWhiteSpace(_options.EngineBaseUrl))
        {
            Warning = "DASHBOARD_ENGINE_BASE_URL is not configured. Set it to point at the engine.";
            return;
        }

        var health = await _healthClient.GetHealthAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (health.Success && health.Raw is not null && health.Document is not null)
        {
            HealthJson = JsonSerializer.Serialize(health.Document, new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            HealthError = health.Error;
        }

        var metrics = await _metricsClient.GetMetricsAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (metrics.Success)
        {
            MetricsRaw = metrics.Text;
        }
        else
        {
            MetricsError = metrics.Error;
        }
    }
}
