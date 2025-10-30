using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Linq;
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
builder.Services.AddHttpClient("oanda-stream", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
});
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
    if (adapterContext.StreamSettings is not null)
    {
        options.EnableStreamingFeed = adapterContext.StreamSettings.Enable;
        if (adapterContext.StreamSettings.HeartbeatTimeout > TimeSpan.Zero)
        {
            options.StreamStaleThreshold = adapterContext.StreamSettings.HeartbeatTimeout;
        }
    }
    var streamEnableEnv = Environment.GetEnvironmentVariable("ENGINE_HOST_ENABLE_STREAM");
    if (!string.IsNullOrWhiteSpace(streamEnableEnv) && bool.TryParse(streamEnableEnv, out var streamEnabled))
    {
        options.EnableStreamingFeed = streamEnabled;
    }
    var streamStaleEnv = Environment.GetEnvironmentVariable("ENGINE_HOST_STREAM_STALE_SECONDS");
    if (!string.IsNullOrWhiteSpace(streamStaleEnv) &&
        double.TryParse(streamStaleEnv, NumberStyles.Float, CultureInfo.InvariantCulture, out var streamStaleSeconds) &&
        streamStaleSeconds > 0)
    {
        options.StreamStaleThreshold = TimeSpan.FromSeconds(streamStaleSeconds);
    }
    var streamAlertEnv = Environment.GetEnvironmentVariable("ENGINE_HOST_STREAM_ALERT_THRESHOLD");
    if (!string.IsNullOrWhiteSpace(streamAlertEnv) &&
        int.TryParse(streamAlertEnv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var alertThreshold) &&
        alertThreshold > 0)
    {
        options.StreamAlertThreshold = alertThreshold;
    }
    var timeframesEnv = Environment.GetEnvironmentVariable("ENGINE_HOST_TIMEFRAMES");
    if (!string.IsNullOrWhiteSpace(timeframesEnv))
    {
        var frames = timeframesEnv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToArray();
        if (frames.Length > 0)
        {
            options.Timeframes.Clear();
            options.Timeframes.AddRange(frames);
        }
    }
    var skewEnv = Environment.GetEnvironmentVariable("ENGINE_HOST_DECISION_SKEW_MS");
    if (!string.IsNullOrWhiteSpace(skewEnv) &&
        double.TryParse(skewEnv, NumberStyles.Float, CultureInfo.InvariantCulture, out var skewMs) &&
        skewMs >= 0)
    {
        options.DecisionSkewToleranceMilliseconds = skewMs;
    }
    var snapshotEnv = Environment.GetEnvironmentVariable("ENGINE_HOST_SNAPSHOT_PATH");
    if (!string.IsNullOrWhiteSpace(snapshotEnv))
    {
        options.SnapshotPath = snapshotEnv;
    }
    var enableLoopEnv = Environment.GetEnvironmentVariable("ENGINE_HOST_ENABLE_LOOP");
    if (!string.IsNullOrWhiteSpace(enableLoopEnv) && bool.TryParse(enableLoopEnv, out var loopEnabled))
    {
        options.EnableContinuousLoop = loopEnabled;
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
    if (adapterContext.StreamSettings is not null)
    {
        builder.Services.AddSingleton(adapterContext.StreamSettings);
    }
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
if (adapterContext.StreamSettings is not null)
{
    builder.Services.AddHostedService<EngineLoopService>();
}

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
    OandaStreamSettings? streamSettings = null;
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
                streamSettings = OandaStreamSettings.FromJson(adapterNode, raw.RootElement, sourceAdapter);
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

    return new AdapterContext(sourceAdapter, ctraderSettings, oandaSettings, streamSettings, featureFlags);
}

internal sealed record AdapterContext(string SourceAdapter, CTraderAdapterSettings? CTraderSettings, OandaAdapterSettings? OandaSettings, OandaStreamSettings? StreamSettings, IReadOnlyList<string> FeatureFlags)
{
    public EngineHostState State { get; } = new EngineHostState(SourceAdapter, FeatureFlags);
}

internal sealed record EngineHostConfiguration(string ConfigPath, string ConfigHash);

public partial class Program;

