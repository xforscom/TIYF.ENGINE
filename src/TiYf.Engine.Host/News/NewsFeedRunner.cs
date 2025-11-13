using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TiYf.Engine.Core;

namespace TiYf.Engine.Host.News;

internal sealed class NewsFeedRunner : IAsyncDisposable
{
    private readonly INewsFeed _feed;
    private readonly EngineHostState _state;
    private readonly Action<IReadOnlyList<NewsEvent>>? _onEventsUpdated;
    private readonly NewsBlackoutConfig _config;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;
    private readonly Func<DateTime> _utcNow;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;
    private readonly List<NewsEvent> _events = new();
    private DateTime? _lastSeenUtc;
    private int _lastSeenOccurrencesAtUtc;
    private long _eventsFetchedTotal;

    private NewsFeedRunner(INewsFeed feed, EngineHostState state, Action<IReadOnlyList<NewsEvent>>? onEventsUpdated, NewsBlackoutConfig config, TimeSpan pollInterval, ILogger logger, Func<DateTime> utcNow)
    {
        _feed = feed;
        _state = state;
        _onEventsUpdated = onEventsUpdated;
        _config = config;
        _pollInterval = pollInterval;
        _logger = logger;
        _utcNow = utcNow;
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public static NewsFeedRunner Start(INewsFeed feed, EngineHostState state, Action<IReadOnlyList<NewsEvent>>? onEventsUpdated, NewsBlackoutConfig config, ILogger logger, Func<DateTime>? utcNow = null)
    {
        var intervalSeconds = Math.Max(5, config.PollSeconds);
        return new NewsFeedRunner(feed, state, onEventsUpdated, config, TimeSpan.FromSeconds(intervalSeconds), logger, utcNow ?? (() => DateTime.UtcNow));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await FetchAndUpdateAsync(initial: true, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await FetchAndUpdateAsync(initial: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FetchAndUpdateAsync(bool initial, CancellationToken cancellationToken)
    {
        try
        {
            var newEvents = await _feed.FetchAsync(_lastSeenUtc, _lastSeenOccurrencesAtUtc, cancellationToken).ConfigureAwait(false);
            if (newEvents.Count > 0)
            {
                _events.AddRange(newEvents);
                _events.Sort((a, b) => a.Utc.CompareTo(b.Utc));
                _lastSeenUtc = _events[^1].Utc;
                _lastSeenOccurrencesAtUtc = CountOccurrencesFromEnd(_lastSeenUtc.Value);
                _eventsFetchedTotal += newEvents.Count;
                _onEventsUpdated?.Invoke(_events.ToArray());
            }

            UpdateTelemetry();
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "News feed poll failed");
            if (initial)
            {
                UpdateTelemetry();
            }
        }
    }

    private void UpdateTelemetry()
    {
        var snapshot = _events.ToArray();
        var (start, end) = ComputeCurrentBlackoutWindow(snapshot);
        _state.UpdateNewsTelemetry(
            _lastSeenUtc,
            _eventsFetchedTotal,
            start.HasValue && end.HasValue,
            start,
            end);
    }

    private (DateTime?, DateTime?) ComputeCurrentBlackoutWindow(IReadOnlyList<NewsEvent> events)
    {
        if (!_config.Enabled || events.Count == 0)
        {
            return (null, null);
        }

        var now = _utcNow();
        foreach (var ev in events)
        {
            var start = ev.Utc.AddMinutes(-_config.MinutesBefore);
            var end = ev.Utc.AddMinutes(_config.MinutesAfter);
            if (now >= start && now <= end)
            {
                return (start, end);
            }
        }

        return (null, null);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("News feed runner cancelled during disposal.");
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private int CountOccurrencesFromEnd(DateTime targetUtc)
    {
        var count = 0;
        for (var i = _events.Count - 1; i >= 0; i--)
        {
            if (_events[i].Utc != targetUtc)
            {
                break;
            }
            count++;
        }
        return count;
    }
}
