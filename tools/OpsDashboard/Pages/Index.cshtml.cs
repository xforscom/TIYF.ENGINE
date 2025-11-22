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
    public HealthSnapshot? HealthSnapshot { get; private set; }
    public DateTime LastUpdatedUtc { get; private set; }
    public int RefreshSeconds { get; private set; }
    public IReadOnlyDictionary<string, List<MetricSample>> ParsedMetrics { get; private set; } =
        new Dictionary<string, List<MetricSample>>(StringComparer.Ordinal);
    public string SummaryStatus { get; private set; } = "Unknown";
    public string SummaryBadgeClass { get; private set; } = "secondary";

    public async Task OnGetAsync(int? refresh = null)
    {
        if (string.IsNullOrWhiteSpace(_options.EngineBaseUrl))
        {
            Warning = "DASHBOARD_EngineBaseUrl is not configured. Set it to point at the engine.";
            return;
        }

        RefreshSeconds = refresh.GetValueOrDefault(30);
        LastUpdatedUtc = DateTime.UtcNow;

        var health = await _healthClient.GetHealthAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (health.Success && health.Raw is not null && health.Document is not null)
        {
            HealthJson = JsonSerializer.Serialize(health.Document, new JsonSerializerOptions { WriteIndented = true });
            HealthSnapshot = HealthSnapshot.FromJson(health.Document);
            (SummaryStatus, SummaryBadgeClass) = HealthSnapshot.EvaluateStatus();
        }
        else
        {
            HealthError = health.Error;
        }

        var metrics = await _metricsClient.GetMetricsAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (metrics.Success)
        {
            MetricsRaw = metrics.Text;
            ParsedMetrics = new MetricsParser().Parse(metrics.Text);
        }
        else
        {
            MetricsError = metrics.Error;
        }
    }
}
