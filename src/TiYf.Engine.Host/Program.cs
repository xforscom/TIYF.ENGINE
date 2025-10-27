using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TiYf.Engine.Host;
using TiYf.Engine.Sim;

var configPath = ResolveConfigPath(args);
var (engineConfig, configHash, rawConfig) = EngineConfigLoader.Load(configPath);
var adapterContext = ResolveAdapterContext(engineConfig, rawConfig);

var builder = WebApplication.CreateBuilder(args);
var portEnv = Environment.GetEnvironmentVariable("ENGINE_HOST_PORT");
var listenPort = 8080;
if (!string.IsNullOrWhiteSpace(portEnv) && int.TryParse(portEnv, out var parsedPort) && parsedPort > 0)
{
    listenPort = parsedPort;
}
builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(listenPort));
builder.Services.AddHttpClient();
builder.Services.AddSingleton(new EngineHostConfiguration(configPath, configHash));
builder.Services.AddSingleton(adapterContext.State);
builder.Services.Configure<EngineHostOptions>(options =>
{
    var heartbeatEnv = Environment.GetEnvironmentVariable("ENGINE_HOST_HEARTBEAT_SECONDS");
    if (!string.IsNullOrWhiteSpace(heartbeatEnv) &&
        double.TryParse(heartbeatEnv, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
        seconds > 0)
    {
        options.HeartbeatInterval = TimeSpan.FromSeconds(seconds);
    }
    var metricsEnv = Environment.GetEnvironmentVariable("ENGINE_HOST_ENABLE_METRICS");
    if (!string.IsNullOrWhiteSpace(metricsEnv) && bool.TryParse(metricsEnv, out var metricsEnabled))
    {
        options.EnableMetrics = metricsEnabled;
    }
});
if (adapterContext.CTraderSettings is not null)
{
    builder.Services.AddSingleton(adapterContext.CTraderSettings);
    builder.Services.AddSingleton<CTraderOpenApiExecutionAdapter>(sp =>
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
        var settings = sp.GetRequiredService<CTraderAdapterSettings>();
        if (settings.BaseUri.IsAbsoluteUri)
        {
            httpClient.BaseAddress = settings.BaseUri;
        }
        httpClient.Timeout = settings.RequestTimeout;
        var state = sp.GetRequiredService<EngineHostState>();
        var logger = sp.GetRequiredService<ILogger<CTraderOpenApiExecutionAdapter>>();
        return new CTraderOpenApiExecutionAdapter(httpClient, settings, line =>
        {
            logger.LogInformation("{Message}", line);
            state.SetLastLog(line);
            return Task.CompletedTask;
        });
    });
    builder.Services.AddSingleton<IConnectableExecutionAdapter>(sp => sp.GetRequiredService<CTraderOpenApiExecutionAdapter>());
}
else if (adapterContext.OandaSettings is not null)
{
    builder.Services.AddSingleton(adapterContext.OandaSettings);
    builder.Services.AddSingleton<OandaRestExecutionAdapter>(sp =>
    {
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
        var settings = sp.GetRequiredService<OandaAdapterSettings>();
        if (settings.BaseUri.IsAbsoluteUri)
        {
            httpClient.BaseAddress = settings.BaseUri;
        }
        httpClient.Timeout = settings.RequestTimeout;
        var state = sp.GetRequiredService<EngineHostState>();
        var logger = sp.GetRequiredService<ILogger<OandaRestExecutionAdapter>>();
        return new OandaRestExecutionAdapter(httpClient, settings, line =>
        {
            logger.LogInformation("{Message}", line);
            state.SetLastLog(line);
            return Task.CompletedTask;
        });
    });
    builder.Services.AddSingleton<IConnectableExecutionAdapter>(sp => sp.GetRequiredService<OandaRestExecutionAdapter>());
}
builder.Services.AddHostedService<EngineHostService>();

if (OperatingSystem.IsLinux())
{
    builder.Host.UseSystemd();
}

var app = builder.Build();
app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("TiYf Engine Host started. Config={ConfigPath} Adapter={Adapter} Port={Port}", configPath, adapterContext.State.Adapter, listenPort);
});

app.MapGet("/health", (EngineHostState state) => Results.Json(state.CreateHealthPayload()));
app.MapGet("/", () => Results.Json(new { status = "ok" }));
var hostOptions = app.Services.GetRequiredService<IOptions<EngineHostOptions>>().Value;
if (hostOptions.EnableMetrics)
{
    app.MapGet("/metrics", (EngineHostState state) =>
    {
        var snapshot = state.CreateMetricsSnapshot();
        var content = EngineMetricsFormatter.Format(snapshot);
        return Results.Text(content, "text/plain");
    });
}
else
{
    app.MapGet("/metrics", () => Results.Text("# metrics disabled\n", "text/plain"));
}

app.MapPost("/shutdown", async (IHostApplicationLifetime lifetime, ILogger<Program> logger) =>
{
    logger.LogWarning("Shutdown requested via /shutdown");
    await Task.Run(() => lifetime.StopApplication());
    return Results.Accepted();
});

app.Run();

static string ResolveConfigPath(string[] args)
{
    for (int i = 0; i < args.Length; i++)
    {
        if ((args[i] == "--config" || args[i] == "-c") && i + 1 < args.Length)
        {
            return Path.GetFullPath(args[i + 1]);
        }
    }
    return Path.GetFullPath("sample-config.demo-ctrader.json");
}

static AdapterContext ResolveAdapterContext(EngineConfig config, JsonDocument raw)
{
    string sourceAdapter = string.IsNullOrWhiteSpace(config.AdapterId) ? "stub" : config.AdapterId.Trim().ToLowerInvariant();
    CTraderAdapterSettings? ctraderSettings = null;
    OandaAdapterSettings? oandaSettings = null;
    if (raw.RootElement.TryGetProperty("adapter", out var adapterNode) && adapterNode.ValueKind == JsonValueKind.Object)
    {
        var typeName = adapterNode.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            sourceAdapter = typeName.Trim().ToLowerInvariant();
            if (sourceAdapter.StartsWith("ctrader", StringComparison.Ordinal))
            {
                ctraderSettings = CTraderAdapterSettings.FromJson(adapterNode, sourceAdapter);
            }
            else if (sourceAdapter.StartsWith("oanda", StringComparison.Ordinal))
            {
                oandaSettings = OandaAdapterSettings.FromJson(adapterNode, sourceAdapter);
            }
        }
    }

    var featureFlags = new List<string>();
    if (raw.RootElement.TryGetProperty("featureFlags", out var featureNode) && featureNode.ValueKind == JsonValueKind.Object)
    {
        foreach (var prop in featureNode.EnumerateObject())
        {
            featureFlags.Add($"{prop.Name}={prop.Value.GetString() ?? prop.Value.ToString()}");
        }
    }

    return new AdapterContext(sourceAdapter, ctraderSettings, oandaSettings, featureFlags);
}

internal sealed record AdapterContext(string SourceAdapter, CTraderAdapterSettings? CTraderSettings, OandaAdapterSettings? OandaSettings, IReadOnlyList<string> FeatureFlags)
{
    public EngineHostState State { get; } = new EngineHostState(SourceAdapter, FeatureFlags);
}

internal sealed record EngineHostConfiguration(string ConfigPath, string ConfigHash);

public partial class Program;

