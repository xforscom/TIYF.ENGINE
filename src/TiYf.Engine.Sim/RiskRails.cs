using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using TiYf.Engine.Core;

namespace TiYf.Engine.Sim;

internal sealed record RiskRailAlert(string EventType, JsonElement Payload, bool Throttled);

internal sealed record RiskRailOutcome(bool Allowed, long Units, IReadOnlyList<RiskRailAlert> Alerts)
{
    public static RiskRailOutcome Permitted(long units) => new(true, units, Array.Empty<RiskRailAlert>());
}

internal sealed class RiskRailRuntime
{
    private readonly RiskConfig? _config;
    private readonly string _configHash;
    private IReadOnlyList<NewsEvent> _newsEvents = Array.Empty<NewsEvent>();
    private readonly Action<string, bool>? _gateCallback;
    private readonly Dictionary<string, decimal> _lastCloseBySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly decimal _startingEquity;
    private DateTime _dailyAnchor = DateTime.MinValue;
    private decimal _dailyRealizedPnl;
    private decimal _dailyUnrealizedPnl;
    private decimal _totalRealizedPnl;
    private decimal _totalUnrealizedPnl;
    private decimal _equityPeak;
    private decimal _currentEquity;

    public RiskRailRuntime(
        RiskConfig? config,
        string riskConfigHash,
        IReadOnlyList<NewsEvent> newsEvents,
        Action<string, bool>? gateCallback,
        decimal startingEquity)
    {
        _config = config;
        _configHash = riskConfigHash ?? string.Empty;
        ReplaceNewsEvents(newsEvents ?? Array.Empty<NewsEvent>());
        _gateCallback = gateCallback;
        _startingEquity = startingEquity <= 0m ? 100_000m : startingEquity;
        _equityPeak = _startingEquity;
        _currentEquity = _startingEquity;
    }

    public void ReplaceNewsEvents(IReadOnlyList<NewsEvent> events)
    {
        _newsEvents = events ?? Array.Empty<NewsEvent>();
    }

    public decimal DailyPnl => _dailyRealizedPnl + _dailyUnrealizedPnl;
    public decimal CurrentEquity => _currentEquity;
    public decimal CurrentDrawdown => _currentEquity - _equityPeak;

    public void UpdateBar(Bar bar, PositionTracker? positions)
    {
        if (bar is null) throw new ArgumentNullException(nameof(bar));
        _lastCloseBySymbol[bar.InstrumentId.Value] = bar.Close;
        var now = DateTime.SpecifyKind(bar.EndUtc, DateTimeKind.Utc);
        var dayAnchor = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
        if (_dailyAnchor != dayAnchor)
        {
            _dailyAnchor = dayAnchor;
        }

        var realized = ComputeRealizedPnl(positions);
        var unrealized = ComputeUnrealizedPnl(positions);
        _totalRealizedPnl = realized;
        _totalUnrealizedPnl = unrealized;
        _currentEquity = _startingEquity + realized + unrealized;
        if (_currentEquity > _equityPeak)
        {
            _equityPeak = _currentEquity;
        }

        _dailyRealizedPnl = ComputeDailyRealizedPnl(positions, dayAnchor);
        _dailyUnrealizedPnl = unrealized;
    }

    public RiskRailOutcome EvaluateNewEntry(string instrument, string timeframe, DateTime decisionUtc, long requestedUnits)
    {
        if (_config is null || requestedUnits <= 0)
        {
            return RiskRailOutcome.Permitted(requestedUnits);
        }

        bool allowed = true;
        long units = requestedUnits;
        var alerts = new List<RiskRailAlert>();
        var ts = DateTime.SpecifyKind(decisionUtc, DateTimeKind.Utc);

        if (_config.SessionWindow is { } session && SessionWindowBlocks(ts, session))
        {
            var payload = new
            {
                instrument,
                timeframe,
                ts,
                start_utc = session.StartUtc.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                end_utc = session.EndUtc.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                config_hash = _configHash
            };
            alerts.Add(CreateAlert("ALERT_BLOCK_SESSION_WINDOW", payload, throttled: false));
            _gateCallback?.Invoke("session_window", false);
            allowed = false;
        }

        if (allowed && _config.DailyCap is { } dailyCap)
        {
            var pnl = DailyPnl;
            if (dailyCap.LossThreshold is { } loss && pnl <= loss)
            {
                var payload = new
                {
                    instrument,
                    timeframe,
                    ts,
                    pnl,
                    loss_threshold = loss,
                    config_hash = _configHash
                };
                alerts.Add(CreateAlert("ALERT_BLOCK_DAILY_LOSS_CAP", payload, throttled: false));
                _gateCallback?.Invoke("daily_loss_cap", false);
                allowed = false;
            }
            else if (dailyCap.GainThreshold is { } gain && pnl >= gain)
            {
                if (dailyCap.Action == DailyCapAction.Block)
                {
                    var payload = new
                    {
                        instrument,
                        timeframe,
                        ts,
                        pnl,
                        gain_threshold = gain,
                        config_hash = _configHash
                    };
                    alerts.Add(CreateAlert("ALERT_BLOCK_DAILY_GAIN_CAP", payload, throttled: false));
                    _gateCallback?.Invoke("daily_gain_cap", false);
                    allowed = false;
                }
                else if (dailyCap.Action == DailyCapAction.HalfSize)
                {
                    var adjusted = Math.Max(1, units / 2);
                    if (adjusted < units)
                    {
                        units = adjusted;
                    }
                    var payload = new
                    {
                        instrument,
                        timeframe,
                        ts,
                        pnl,
                        gain_threshold = gain,
                        original_units = requestedUnits,
                        adjusted_units = units,
                        config_hash = _configHash
                    };
                    alerts.Add(CreateAlert("ALERT_THROTTLE_DAILY_GAIN_CAP", payload, throttled: true));
                    _gateCallback?.Invoke("daily_gain_cap", true);
                }
            }
        }

        if (allowed && _config.GlobalDrawdown is { } globalDd)
        {
            var drawdown = CurrentDrawdown;
            if (drawdown < globalDd.MaxDrawdown)
            {
                var payload = new
                {
                    instrument,
                    timeframe,
                    ts,
                    drawdown,
                    max_dd = globalDd.MaxDrawdown,
                    equity = _currentEquity,
                    peak_equity = _equityPeak,
                    config_hash = _configHash
                };
                alerts.Add(CreateAlert("ALERT_BLOCK_GLOBAL_DRAWDOWN", payload, throttled: false));
                _gateCallback?.Invoke("global_drawdown", false);
                allowed = false;
            }
        }

        if (allowed && _config.NewsBlackout is { Enabled: true } blackout)
        {
            var match = FindMatchingNewsEvent(instrument, ts, blackout.MinutesBefore, blackout.MinutesAfter);
            if (match is NewsEvent ev)
            {
                var payload = new
                {
                    instrument,
                    timeframe,
                    ts,
                    event_utc = ev.Utc,
                    impact = ev.Impact,
                    tags = ev.Tags,
                    config_hash = _configHash
                };
                alerts.Add(CreateAlert("ALERT_BLOCK_NEWS_BLACKOUT", payload, throttled: false));
                _gateCallback?.Invoke("news_blackout", false);
                allowed = false;
            }
        }

        return new RiskRailOutcome(allowed, allowed ? units : 0, alerts);
    }

    private RiskRailAlert CreateAlert(string eventType, object payload, bool throttled)
    {
        var element = JsonSerializer.SerializeToElement(payload, _jsonOptions);
        return new RiskRailAlert(eventType, element, throttled);
    }

    private bool SessionWindowBlocks(DateTime ts, SessionWindowConfig config)
    {
        var time = ts.TimeOfDay;
        var start = config.StartUtc;
        var end = config.EndUtc;
        if (start == end) return false; // treat as open window
        var inWindow = start < end
            ? time >= start && time < end
            : time >= start || time < end;
        return !inWindow;
    }

    private NewsEvent? FindMatchingNewsEvent(string instrument, DateTime ts, int minutesBefore, int minutesAfter)
    {
        if (_newsEvents.Count == 0) return null;
        foreach (var ev in _newsEvents)
        {
            if (!ev.MatchesInstrument(instrument)) continue;
            var windowStart = ev.Utc.AddMinutes(-minutesBefore);
            var windowEnd = ev.Utc.AddMinutes(minutesAfter);
            if (ts >= windowStart && ts <= windowEnd)
            {
                return ev;
            }
        }
        return null;
    }

    public void ForceDrawdown(decimal limit)
    {
        var abs = Math.Abs(limit);
        var peak = _equityPeak <= 0m ? _startingEquity : _equityPeak;
        _equityPeak = peak;
        _currentEquity = Math.Max(0m, peak - (abs + 1m));
    }

    private static decimal ComputeRealizedPnl(PositionTracker? positions)
    {
        if (positions is null) return 0m;
        return positions.Completed.Sum(t => t.PnlCcy);
    }

    private decimal ComputeUnrealizedPnl(PositionTracker? positions)
    {
        if (positions is null) return 0m;
        decimal total = 0m;
        foreach (var pos in positions.SnapshotOpenPositions())
        {
            if (!_lastCloseBySymbol.TryGetValue(pos.Symbol, out var lastPrice)) continue;
            var dir = pos.Side == TradeSide.Buy ? 1m : -1m;
            var pnl = (lastPrice - pos.EntryPrice) * dir * pos.Units;
            total += pnl;
        }
        return decimal.Round(total, 2, MidpointRounding.AwayFromZero);
    }

    private static decimal ComputeDailyRealizedPnl(PositionTracker? positions, DateTime dayAnchor)
    {
        if (positions is null) return 0m;
        decimal total = 0m;
        foreach (var trade in positions.Completed)
        {
            if (trade.UtcTsClose >= dayAnchor)
            {
                total += trade.PnlCcy;
            }
        }
        return total;
    }
}
