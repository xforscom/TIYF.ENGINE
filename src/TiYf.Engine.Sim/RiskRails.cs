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

internal enum RiskRailsMode
{
    Disabled,
    Telemetry,
    Live
}

internal readonly record struct RiskPositionUnits(string Symbol, long Units);

public readonly record struct BrokerCaps(
    decimal? DailyLossCapCcy,
    long? MaxUnits,
    IReadOnlyDictionary<string, long>? SymbolUnitCaps);

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
    private readonly decimal? _brokerDailyLossCapCcy;
    private readonly long? _maxPositionUnits;
    private readonly IReadOnlyDictionary<string, long>? _symbolUnitCaps;
    private readonly BrokerCaps? _brokerCaps;
    private readonly RiskCooldownConfig _cooldownConfig;
    private readonly Action<RiskRailTelemetrySnapshot>? _telemetryCallback;
    private readonly Func<DateTime> _clock;
    private decimal _brokerDailyLossUsedCcy;
    private long _brokerDailyLossViolations;
    private long _maxPositionUnitsUsed;
    private long _maxPositionViolations;
    private readonly Dictionary<string, long> _symbolUnitUsage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _symbolUnitViolations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _brokerCapBlocksByGate = new(StringComparer.OrdinalIgnoreCase);
    private long _brokerCapBlocksTotal;
    private DateTime? _cooldownActiveUntilUtc;
    private long _cooldownTriggersTotal;
    private DateTime? _cooldownLastTriggerUtc;
    private int _cooldownLossStreak;
    private int _lastCompletedTradesCount;
    private bool _cooldownAlertPending;
    private bool _cooldownActiveAlerted;
    private readonly RiskRailsMode _mode;
    private readonly bool _blockingEnabled;

    public RiskRailRuntime(
        RiskConfig? config,
        string riskConfigHash,
        IReadOnlyList<NewsEvent> newsEvents,
        Action<string, bool>? gateCallback,
        decimal startingEquity,
        Action<RiskRailTelemetrySnapshot>? telemetryCallback = null,
        Func<DateTime>? clock = null,
        BrokerCaps? brokerCaps = null,
        bool enableBlocking = true)
    {
        _config = config;
        _configHash = riskConfigHash ?? string.Empty;
        ReplaceNewsEvents(newsEvents ?? Array.Empty<NewsEvent>());
        _gateCallback = gateCallback;
        _startingEquity = startingEquity <= 0m ? 100_000m : startingEquity;
        _equityPeak = _startingEquity;
        _currentEquity = _startingEquity;
        _brokerDailyLossCapCcy = config?.BrokerDailyLossCapCcy;
        _maxPositionUnits = (config?.MaxPositionUnits is { } maxUnits && maxUnits > 0) ? maxUnits : null;
        _symbolUnitCaps = config?.SymbolUnitCaps is { Count: > 0 } caps
            ? new Dictionary<string, long>(caps, StringComparer.OrdinalIgnoreCase)
            : null;
        _brokerCaps = brokerCaps;
        _cooldownConfig = config?.Cooldown ?? RiskCooldownConfig.Disabled;
        _telemetryCallback = telemetryCallback;
        _clock = clock ?? (() => DateTime.UtcNow);
        _mode = ResolveMode(config?.RiskRailsMode);
        _blockingEnabled = enableBlocking;
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
        UpdateCooldownState(positions);
        UpdateSymbolUsageFromPositions(positions);
        UpdateBrokerDailyLossUsage();
        PublishTelemetry();
    }

    public RiskRailOutcome EvaluateNewEntry(
        string instrument,
        string timeframe,
        DateTime decisionUtc,
        long requestedUnits,
        IReadOnlyCollection<RiskPositionUnits>? openPositions = null)
    {
        if (_mode == RiskRailsMode.Disabled)
        {
            PublishTelemetry();
            return RiskRailOutcome.Permitted(requestedUnits);
        }

        UpdateSymbolUsageFromSnapshot(openPositions?.Select(p => (p.Symbol, p.Units)));
        UpdateBrokerDailyLossUsage();
        RefreshCooldownWindow();

        if (_config is null || requestedUnits <= 0)
        {
            PublishTelemetry();
            return RiskRailOutcome.Permitted(requestedUnits);
        }

        bool allowed = true;
        long units = requestedUnits;
        var alerts = new List<RiskRailAlert>();
        var ts = DateTime.SpecifyKind(decisionUtc, DateTimeKind.Utc);

        if (_blockingEnabled && _config.SessionWindow is { } session && SessionWindowBlocks(ts, session))
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

        if (_blockingEnabled && allowed && _config.DailyCap is { } dailyCap)
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

        if (_blockingEnabled && allowed && _config.GlobalDrawdown is { } globalDd)
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

        if (_blockingEnabled && allowed && _config.NewsBlackout is { Enabled: true } blackout)
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

        var liveRails = _mode == RiskRailsMode.Live && _blockingEnabled;
        if (liveRails)
        {
            EvaluateLiveRails(instrument, timeframe, ts, requestedUnits, alerts, ref allowed);
        }
        else
        {
            EvaluateTelemetryRails(instrument, timeframe, ts, requestedUnits, alerts);
        }
        EvaluateBrokerGuardrail(instrument, timeframe, ts, requestedUnits, alerts, ref allowed, liveRails);
        PublishTelemetry();
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

    /// <summary>
    /// Evaluates telemetry-only rails (caps and cooldown) and emits soft alerts without blocking.
    /// </summary>
    private void EvaluateTelemetryRails(string instrument, string timeframe, DateTime ts, long requestedUnits, List<RiskRailAlert> alerts)
    {
        EvaluatePositionCaps(instrument, timeframe, ts, requestedUnits, alerts);
        EvaluateCooldown(instrument, timeframe, ts, alerts);
    }

    private void EvaluateLiveRails(string instrument, string timeframe, DateTime ts, long requestedUnits, List<RiskRailAlert> alerts, ref bool allowed)
    {
        EvaluatePositionCapsLive(instrument, timeframe, ts, requestedUnits, alerts, ref allowed);
        if (allowed)
        {
            EvaluateCooldownLive(instrument, timeframe, ts, alerts, ref allowed);
        }
        // Always emit telemetry so metrics/health remain populated.
        EvaluateTelemetryRails(instrument, timeframe, ts, requestedUnits, alerts);
    }

    private void EvaluateBrokerGuardrail(string instrument, string timeframe, DateTime ts, long requestedUnits, List<RiskRailAlert> alerts, ref bool allowed, bool liveRails)
    {
        if (requestedUnits <= 0)
        {
            return;
        }

        var dailyCap = _brokerCaps?.DailyLossCapCcy ?? _brokerDailyLossCapCcy;
        var maxUnits = _brokerCaps?.MaxUnits ?? _maxPositionUnits;
        var symbolCaps = _brokerCaps?.SymbolUnitCaps ?? _symbolUnitCaps;

        if (!dailyCap.HasValue && maxUnits is null && symbolCaps is null)
        {
            return;
        }

        string? gate = null;
        JsonElement payload = default;

        if (dailyCap.HasValue && _brokerDailyLossUsedCcy >= dailyCap.Value)
        {
            gate = "daily_loss";
            payload = JsonSerializer.SerializeToElement(new
            {
                instrument,
                timeframe,
                ts,
                loss_used_ccy = _brokerDailyLossUsedCcy,
                loss_cap_ccy = dailyCap.Value,
                config_hash = _configHash
            }, _jsonOptions);
        }

        var unitsAbs = Math.Abs(requestedUnits);
        if (gate is null && maxUnits.HasValue)
        {
            var projectedGlobal = _symbolUnitUsage.Values.Sum() + unitsAbs;
            if (projectedGlobal > maxUnits.Value)
            {
                gate = "global_units";
                payload = JsonSerializer.SerializeToElement(new
                {
                    instrument,
                    timeframe,
                    ts,
                    used_units = projectedGlobal,
                    max_units = maxUnits.Value,
                    config_hash = _configHash
                }, _jsonOptions);
            }
        }

        if (gate is null && symbolCaps is not null && symbolCaps.TryGetValue(instrument, out var symCap))
        {
            var current = _symbolUnitUsage.TryGetValue(instrument, out var v) ? v : 0L;
            var projectedSymbol = current + unitsAbs;
            if (projectedSymbol > symCap)
            {
                gate = $"symbol_units:{instrument}";
                payload = JsonSerializer.SerializeToElement(new
                {
                    instrument,
                    timeframe,
                    ts,
                    used_units = projectedSymbol,
                    max_units = symCap,
                    config_hash = _configHash
                }, _jsonOptions);
            }
        }

        if (gate is null)
        {
            return;
        }

        alerts.Add(CreateAlert("ALERT_RISK_BROKER_CAP_SOFT", payload, throttled: true));
        if (gate == "daily_loss")
        {
            _brokerDailyLossViolations++;
        }
        _brokerCapBlocksTotal++;
        _brokerCapBlocksByGate[gate] = _brokerCapBlocksByGate.TryGetValue(gate, out var existing) ? existing + 1 : 1;

        if (liveRails && allowed)
        {
            alerts.Add(CreateAlert("ALERT_RISK_BROKER_CAP_HARD", payload, throttled: false));
            _gateCallback?.Invoke(gate, false);
            allowed = false;
        }
    }

    /// <summary>
    /// Records telemetry for global and per-symbol caps without altering execution.
    /// </summary>
    private void EvaluatePositionCaps(string instrument, string timeframe, DateTime ts, long requestedUnits, List<RiskRailAlert> alerts)
    {
        var unitsAbs = Math.Abs(requestedUnits);
        if (unitsAbs <= 0)
        {
            return;
        }

        if (_maxPositionUnits.HasValue)
        {
            var projected = _maxPositionUnitsUsed + unitsAbs;
            if (projected > _maxPositionUnits.Value)
            {
                var payload = new
                {
                    instrument,
                    timeframe,
                    ts,
                    projected_units = projected,
                    max_units = _maxPositionUnits.Value,
                    config_hash = _configHash
                };
                alerts.Add(CreateAlert("ALERT_RISK_MAX_POSITION_SOFT", payload, throttled: true));
                _maxPositionViolations++;
            }
        }

        if (_symbolUnitCaps is not { Count: > 0 })
        {
            return;
        }

        var symbolKey = instrument?.Trim();
        if (string.IsNullOrWhiteSpace(symbolKey))
        {
            return;
        }
        if (_symbolUnitCaps.TryGetValue(symbolKey, out var cap) && cap > 0)
        {
            var current = _symbolUnitUsage.TryGetValue(symbolKey, out var usage) ? usage : 0;
            var projected = current + unitsAbs;
            if (projected > cap)
            {
                var payload = new
                {
                    instrument,
                    timeframe,
                    ts,
                    projected_units = projected,
                    cap_units = cap,
                    config_hash = _configHash
                };
                alerts.Add(CreateAlert("ALERT_RISK_SYMBOL_CAP_SOFT", payload, throttled: true));
                _symbolUnitViolations[symbolKey] = _symbolUnitViolations.TryGetValue(symbolKey, out var existing) ? existing + 1 : 1;
            }
        }
    }

    private void EvaluatePositionCapsLive(string instrument, string timeframe, DateTime ts, long requestedUnits, List<RiskRailAlert> alerts, ref bool allowed)
    {
        var unitsAbs = Math.Abs(requestedUnits);
        if (unitsAbs <= 0 || !allowed)
        {
            return;
        }

        if (_maxPositionUnits.HasValue)
        {
            var projected = _maxPositionUnitsUsed + unitsAbs;
            if (projected > _maxPositionUnits.Value)
            {
                var payload = new
                {
                    instrument,
                    timeframe,
                    ts,
                    projected_units = projected,
                    max_units = _maxPositionUnits.Value,
                    config_hash = _configHash
                };
                alerts.Add(CreateAlert("ALERT_RISK_MAX_POSITION_HARD", payload, throttled: false));
                _gateCallback?.Invoke("max_position_units", false);
                _maxPositionViolations++;
                allowed = false;
            }
        }

        if (!allowed || _symbolUnitCaps is not { Count: > 0 })
        {
            return;
        }

        var symbolKey = instrument?.Trim();
        if (string.IsNullOrWhiteSpace(symbolKey))
        {
            return;
        }

        if (_symbolUnitCaps.TryGetValue(symbolKey, out var cap) && cap > 0)
        {
            var current = _symbolUnitUsage.TryGetValue(symbolKey, out var usage) ? usage : 0;
            var projected = current + unitsAbs;
            if (projected > cap)
            {
                var payload = new
                {
                    instrument,
                    timeframe,
                    ts,
                    projected_units = projected,
                    cap_units = cap,
                    config_hash = _configHash
                };
                alerts.Add(CreateAlert("ALERT_RISK_SYMBOL_CAP_HARD", payload, throttled: false));
                _gateCallback?.Invoke($"symbol_cap:{symbolKey}", false);
                _symbolUnitViolations[symbolKey] = _symbolUnitViolations.TryGetValue(symbolKey, out var existing) ? existing + 1 : 1;
                allowed = false;
            }
        }
    }

    /// <summary>
    /// Emits telemetry describing cooldown state when the guard is active.
    /// </summary>
    private void EvaluateCooldown(string instrument, string timeframe, DateTime ts, List<RiskRailAlert> alerts)
    {
        if (!_cooldownConfig.Enabled)
        {
            return;
        }

        var active = IsCooldownActive();
        if (!active)
        {
            _cooldownAlertPending = false;
            _cooldownActiveAlerted = false;
            return;
        }

        if (_cooldownAlertPending || !_cooldownActiveAlerted)
        {
            var payload = new
            {
                instrument,
                timeframe,
                ts,
                cooldown_active_until = _cooldownActiveUntilUtc,
                cooldown_minutes = _cooldownConfig.CooldownMinutes,
                consecutive_losses = _cooldownConfig.ConsecutiveLosses,
                config_hash = _configHash
            };
            alerts.Add(CreateAlert("ALERT_RISK_COOLDOWN_SOFT", payload, throttled: true));
            _cooldownAlertPending = false;
            _cooldownActiveAlerted = true;
        }
    }

    private void EvaluateCooldownLive(string instrument, string timeframe, DateTime ts, List<RiskRailAlert> alerts, ref bool allowed)
    {
        if (!_cooldownConfig.Enabled || !IsCooldownActive() || !allowed)
        {
            return;
        }

        var payload = new
        {
            instrument,
            timeframe,
            ts,
            cooldown_active_until = _cooldownActiveUntilUtc,
            cooldown_minutes = _cooldownConfig.CooldownMinutes,
            consecutive_losses = _cooldownConfig.ConsecutiveLosses,
            config_hash = _configHash
        };
        alerts.Add(CreateAlert("ALERT_RISK_COOLDOWN_HARD", payload, throttled: false));
        _gateCallback?.Invoke("cooldown", false);
        _cooldownAlertPending = false;
        _cooldownActiveAlerted = true;
        allowed = false;
    }

    private void UpdateSymbolUsageFromPositions(PositionTracker? positions)
    {
        if (positions is null)
        {
            UpdateSymbolUsageFromSnapshot(null);
            return;
        }

        var snapshot = positions.SnapshotOpenPositions();
        if (snapshot.Count == 0)
        {
            UpdateSymbolUsageFromSnapshot(Array.Empty<(string Symbol, long Units)>());
            return;
        }

        UpdateSymbolUsageFromSnapshot(snapshot.Select(pos => (pos.Symbol, pos.Units)));
    }

    private long UpdateSymbolUsageFromSnapshot(IEnumerable<(string Symbol, long Units)>? snapshot)
    {
        _symbolUnitUsage.Clear();
        if (snapshot is null)
        {
            _maxPositionUnitsUsed = 0;
            return 0;
        }

        long total = 0;
        foreach (var entry in snapshot.Where(e => !string.IsNullOrWhiteSpace(e.Symbol)))
        {
            var normalizedSymbol = entry.Symbol!.Trim();
            if (string.IsNullOrWhiteSpace(normalizedSymbol))
            {
                continue;
            }

            var absUnits = Math.Abs(entry.Units);
            if (absUnits <= 0)
            {
                continue;
            }

            total += absUnits;
            _symbolUnitUsage[normalizedSymbol] = _symbolUnitUsage.TryGetValue(normalizedSymbol, out var existing) ? existing + absUnits : absUnits;
        }

        _maxPositionUnitsUsed = total;
        return total;
    }

    private void UpdateBrokerDailyLossUsage()
    {
        var loss = -DailyPnl;
        _brokerDailyLossUsedCcy = loss <= 0m ? 0m : decimal.Round(loss, 2, MidpointRounding.AwayFromZero);
    }

    private void UpdateCooldownState(PositionTracker? positions)
    {
        if (!_cooldownConfig.Enabled)
        {
            _cooldownLossStreak = 0;
            _cooldownActiveUntilUtc = null;
            return;
        }

        if (positions is not null)
        {
            var completed = positions.Completed;
            if (completed.Count > _lastCompletedTradesCount)
            {
                for (var i = _lastCompletedTradesCount; i < completed.Count; i++)
                {
                    var trade = completed[i];
                    if (trade.PnlCcy < 0m)
                    {
                        _cooldownLossStreak++;
                        if (_cooldownConfig.ConsecutiveLosses.HasValue)
                        {
                            var consecutiveLosses = _cooldownConfig.ConsecutiveLosses.Value;
                            if (consecutiveLosses > 0 && _cooldownLossStreak >= consecutiveLosses)
                            {
                                TriggerCooldown(trade.UtcTsClose);
                                _cooldownLossStreak = 0;
                            }
                        }
                    }
                    else if (trade.PnlCcy > 0m)
                    {
                        _cooldownLossStreak = 0;
                    }
                }

                _lastCompletedTradesCount = completed.Count;
            }
        }

        RefreshCooldownWindow();
    }

    private void TriggerCooldown(DateTime triggerUtc)
    {
        if (!_cooldownConfig.Enabled || !_cooldownConfig.CooldownMinutes.HasValue || _cooldownConfig.CooldownMinutes.Value <= 0)
        {
            return;
        }

        var normalized = triggerUtc.Kind == DateTimeKind.Utc ? triggerUtc : DateTime.SpecifyKind(triggerUtc, DateTimeKind.Utc);
        _cooldownActiveUntilUtc = normalized.AddMinutes(_cooldownConfig.CooldownMinutes.Value);
        _cooldownTriggersTotal++;
        _cooldownLastTriggerUtc = normalized;
        _cooldownAlertPending = true;
        _cooldownActiveAlerted = false;
    }

    private void RefreshCooldownWindow()
    {
        if (_cooldownActiveUntilUtc.HasValue && _clock() >= _cooldownActiveUntilUtc.Value)
        {
            _cooldownActiveUntilUtc = null;
            _cooldownAlertPending = false;
            _cooldownActiveAlerted = false;
        }
    }

    private bool IsCooldownActive()
    {
        return _cooldownActiveUntilUtc.HasValue && _clock() < _cooldownActiveUntilUtc.Value;
    }

    /// <summary>
    /// Test-only helper to seed daily PnL for deterministic rail evaluation.
    /// </summary>
    public void SetDailyPnlForTest(decimal realized, decimal unrealized = 0m)
    {
        _dailyRealizedPnl = realized;
        _dailyUnrealizedPnl = unrealized;
    }

    private void PublishTelemetry()
    {
        if (_telemetryCallback is null)
        {
            return;
        }

        var symbolUsage = new Dictionary<string, long>(_symbolUnitUsage, StringComparer.OrdinalIgnoreCase);
        var symbolViolations = new Dictionary<string, long>(_symbolUnitViolations, StringComparer.OrdinalIgnoreCase);
        var snapshot = new RiskRailTelemetrySnapshot(
            _brokerDailyLossCapCcy,
            _brokerDailyLossUsedCcy,
            _brokerDailyLossViolations,
            _maxPositionUnits,
            _maxPositionUnitsUsed,
            _maxPositionViolations,
            _symbolUnitCaps,
            symbolUsage,
            symbolViolations,
            _brokerCapBlocksTotal,
            new Dictionary<string, long>(_brokerCapBlocksByGate, StringComparer.OrdinalIgnoreCase),
            _cooldownConfig.Enabled,
            IsCooldownActive(),
            _cooldownActiveUntilUtc,
            _cooldownTriggersTotal,
            _cooldownConfig.Enabled ? _cooldownConfig.ConsecutiveLosses : null,
            _cooldownConfig.Enabled ? _cooldownConfig.CooldownMinutes : null);
        _telemetryCallback(snapshot);
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

    private static RiskRailsMode ResolveMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return RiskRailsMode.Telemetry;
        return raw.Trim().ToLowerInvariant() switch
        {
            "off" or "disabled" => RiskRailsMode.Disabled,
            "live" or "active" => RiskRailsMode.Live,
            _ => RiskRailsMode.Telemetry
        };
    }
}
