using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TiYf.Engine.Host.Alerts;

public sealed class DiscordAlertSink : IAlertSink, IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly string _webhook;
    private readonly string _environment;
    private readonly Channel<AlertRecord> _queue = Channel.CreateUnbounded<AlertRecord>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpTask;

    public DiscordAlertSink(IHttpClientFactory factory, string webhookUrl, string environment)
    {
        _client = factory.CreateClient("alert-sink");
        _client.Timeout = TimeSpan.FromSeconds(10);
        _webhook = webhookUrl;
        _environment = string.IsNullOrWhiteSpace(environment) ? "demo" : environment;
        _pumpTask = Task.Run(PumpAsync);
    }

    public void Enqueue(AlertRecord alert)
    {
        _queue.Writer.TryWrite(alert);
    }

    private async Task PumpAsync()
    {
        var token = _cts.Token;
        try
        {
            await foreach (var alert in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    var payload = new
                    {
                        content = $"[{_environment}] {alert.Category}/{alert.Severity}: {alert.Summary} ({alert.OccurredUtc:o})"
                    };
                    var json = JsonSerializer.Serialize(payload);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var response = await _client.PostAsync(_webhook, content, token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        // best-effort; log to console without leaking secrets
                        Console.WriteLine($"alert_sink warn status={(int)response.StatusCode}");
                    }
                }
                catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
                {
                    // shutting down or timeout; drop
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"alert_sink error: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("alert_sink info: pump cancelled");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
        _cts.Dispose();
    }
}
