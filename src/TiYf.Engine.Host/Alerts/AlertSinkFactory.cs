using System;
using System.Net.Http;

namespace TiYf.Engine.Host.Alerts;

public static class AlertSinkFactory
{
    public static IAlertSink Create(IHttpClientFactory httpClientFactory, string environmentLabel)
    {
        var sinkType = Environment.GetEnvironmentVariable("ALERT_SINK_TYPE")?.Trim().ToLowerInvariant();
        var discordWebhook = Environment.GetEnvironmentVariable("ALERT_DISCORD_WEBHOOK_URL");
        var filePath = Environment.GetEnvironmentVariable("ALERT_FILE_PATH");

        return sinkType switch
        {
            "discord" when !string.IsNullOrWhiteSpace(discordWebhook) => new DiscordAlertSink(httpClientFactory, discordWebhook!, environmentLabel),
            "file" when !string.IsNullOrWhiteSpace(filePath) => new FileAlertSink(filePath!),
            _ => new NoopAlertSink()
        };
    }
}
