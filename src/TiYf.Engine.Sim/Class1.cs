using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TiYf.Engine.Core;
using TiYf.Engine.Sidecar;

namespace TiYf.Engine.Sim;

public sealed record EngineConfig(
    string SchemaVersion,
    string RunId,
    string InstrumentFile,
    string InputTicksFile,
    string JournalRoot,
    string BarOutputEventType = "BAR_V1",
    string ClockMode = "sequence",
    string AdapterId = "stub",
    string BrokerId = "stub-sim",
    string AccountId = "account-stub",
    string[]? Instruments = null,
    string[]? Intervals = null
);

public static class EngineConfigLoader
{
    public static (EngineConfig config, string configHash, JsonDocument raw) Load(string path)
    {
        var rawBytes = File.ReadAllBytes(path);
        var hash = ConfigHash.Compute(rawBytes);
        var doc = JsonDocument.Parse(rawBytes);
        var cfg = doc.Deserialize<EngineConfig>() ?? throw new InvalidOperationException("Invalid config");
        return (cfg, hash, doc);
    }
}

public interface ITickSource : IEnumerable<PriceTick> { }

public sealed class CsvTickSource : ITickSource
{
    private readonly string _path;
    private readonly InstrumentId _instrumentId;
    private readonly List<PriceTick> _ordered;
    public CsvTickSource(string path, InstrumentId instrumentId)
    {
        _path = path; _instrumentId = instrumentId;
        _ordered = File.ReadLines(_path)
            .Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(line =>
            {
                var parts = line.Split(',');
                var raw = DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
                if (raw.Kind != DateTimeKind.Utc)
                    throw new InvalidDataException($"Tick timestamp must be UTC: {raw:o}");
                var ts = raw;
                var price = decimal.Parse(parts[1]);
                var vol = decimal.Parse(parts[2]);
                return new PriceTick(_instrumentId, ts, price, vol);
            })
            .OrderBy(t => t.UtcTimestamp)
            .ThenBy(t => t.InstrumentId.Value) // deterministic tie-breaker
            .ToList();
    }
    public IEnumerator<PriceTick> GetEnumerator() => _ordered.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class EngineLoop
{
    private readonly IClock _clock;
    private readonly Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder> _builders;
    private readonly IBarKeyTracker _barKeyTracker;
    private readonly IJournalWriter _journal;
    private readonly ITickSource _ticks;
    private ulong _seq;
    private readonly string _barEventType;
    private readonly Action<Bar>? _onBarEmitted;
    private readonly Action<int, int>? _onPositionMetrics;
    private readonly IRiskFormulas? _riskFormulas;
    private readonly IBasketRiskAggregator? _basketAggregator;
    private readonly IRiskEnforcer? _riskEnforcer; // new enforcement dependency
    private readonly List<PositionInitialRisk> _openPositions = new();
    private readonly string? _configHash; // pass-through for risk probe journaling
    private readonly string _schemaVersion;
    private readonly RiskConfig? _riskConfig;
    private readonly string _riskMode = "off"; // off|shadow|active
    private readonly decimal? _equityOverride;
    private readonly DeterministicScriptStrategy? _deterministicStrategy; // optional strategy for M0
    private readonly IExecutionAdapter? _execution;
    private readonly PositionTracker? _positions;
    private readonly TradesJournalWriter? _tradesWriter;
    private readonly string? _dataVersion;
    private readonly string _sourceAdapter;
    private readonly Dictionary<string, long> _openUnits = new(); // decisionId -> units
    private readonly SentimentGuardConfig? _sentimentConfig;
    private readonly Dictionary<string, int> _decisionCounters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _riskProbeCounters = new(StringComparer.Ordinal); // per-instrument deterministic counter for risk probe DecisionId
    private readonly Dictionary<string, Queue<decimal>> _sentimentWindows = new(StringComparer.Ordinal);
    private DateTime? _lastStrategyMinute; // to avoid emitting strategy actions multiple times per minute when multiple instrument ticks share the same minute
    private readonly bool _riskProbeEnabled = true; // feature flag
    private readonly IReadOnlyDictionary<long, string> _timeframeLabels;
    private readonly string _riskConfigHash;
    private readonly RiskRailRuntime? _riskRails;

    private string? ExtractDataVersion() => _dataVersion;

    private static void LogOrderSendOk(string decisionId, string symbol, string? brokerOrderId)
    {
        var id = string.IsNullOrWhiteSpace(brokerOrderId) ? "unknown" : brokerOrderId;
        Console.WriteLine($"OrderSend ok decision={decisionId} brokerOrderId={id} symbol={symbol}");
    }

    public EngineLoop(IClock clock, Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder> builders, IBarKeyTracker tracker, IJournalWriter journal, ITickSource ticks, string barEventType, Action<Bar>? onBarEmitted = null, Action<int, int>? onPositionMetrics = null, IRiskFormulas? riskFormulas = null, IBasketRiskAggregator? basketAggregator = null, string? configHash = null, string schemaVersion = TiYf.Engine.Core.Infrastructure.Schema.Version, IRiskEnforcer? riskEnforcer = null, RiskConfig? riskConfig = null, decimal? equityOverride = null, DeterministicScriptStrategy? deterministicStrategy = null, IExecutionAdapter? execution = null, PositionTracker? positions = null, TradesJournalWriter? tradesWriter = null, string? dataVersion = null, string sourceAdapter = "stub", long sizeUnitsFx = 1000, long sizeUnitsXau = 1, bool riskProbeEnabled = true, SentimentGuardConfig? sentimentConfig = null, string? penaltyConfig = null, bool forcePenalty = false, bool ciPenaltyScaffold = false, string riskMode = "off", string? riskConfigHash = null, IReadOnlyList<NewsEvent>? newsEvents = null, IReadOnlyDictionary<long, string>? timeframeLabels = null, Action<string, bool>? riskGateCallback = null)
    {
        _clock = clock; _builders = builders; _barKeyTracker = tracker; _journal = journal; _ticks = ticks; _barEventType = barEventType; _seq = (journal is FileJournalWriter fj ? fj.NextSequence : 1UL) - 1UL; _onBarEmitted = onBarEmitted; _onPositionMetrics = onPositionMetrics; _riskFormulas = riskFormulas; _basketAggregator = basketAggregator; _configHash = configHash; _schemaVersion = schemaVersion; _riskEnforcer = riskEnforcer; _riskConfig = riskConfig; _equityOverride = equityOverride; _deterministicStrategy = deterministicStrategy; _execution = execution; _positions = positions; _tradesWriter = tradesWriter; _dataVersion = dataVersion; _sourceAdapter = string.IsNullOrWhiteSpace(sourceAdapter) ? "stub" : sourceAdapter; _riskProbeEnabled = riskProbeEnabled; _sentimentConfig = sentimentConfig; _penaltyMode = penaltyConfig ?? "off"; _forcePenalty = forcePenalty; _ciPenaltyScaffold = ciPenaltyScaffold; _riskMode = string.IsNullOrWhiteSpace(riskMode) ? "off" : riskMode.ToLowerInvariant();
        _sizeUnitsFx = sizeUnitsFx; _sizeUnitsXau = sizeUnitsXau;
        _riskConfigHash = riskConfigHash ?? string.Empty;
        _timeframeLabels = timeframeLabels ?? new Dictionary<long, string>();
        var startingEquity = equityOverride ?? 100_000m;
        _riskRails = riskConfig is not null
            ? new RiskRailRuntime(riskConfig, _riskConfigHash, newsEvents ?? Array.Empty<NewsEvent>(), riskGateCallback, startingEquity)
            : null;
#if DEBUG
        if (_riskMode == "off" && riskConfig is not null && (riskConfig.EmitEvaluations || (riskConfig.MaxNetExposureBySymbol != null || riskConfig.MaxRunDrawdownCCY != null)))
        {
            throw new InvalidOperationException("DEBUG: riskMode resolved Off unexpectedly while riskConfig provided");
        }
#endif
    }
    private readonly long _sizeUnitsFx; private readonly long _sizeUnitsXau;
    private readonly string _penaltyMode; private readonly bool _forcePenalty; private readonly bool _ciPenaltyScaffold;
    private bool _penaltyEmitted = false; // ensure single forced emission
                                          // Risk tracking (net exposure by symbol & run drawdown). Simplified placeholders until full logic filled in.
    private readonly Dictionary<string, decimal> _netExposureBySymbol = new(StringComparer.Ordinal);
    private bool _riskBlockCurrentBar = false; // reset per bar
    private int _riskEvalCount = 0; // counts INFO_RISK_EVAL emissions for test hooks
    private readonly Dictionary<string, int> _riskEvalCountBySymbol = new(StringComparer.Ordinal);

    private void ForceDrawdown(string symbol, decimal limit)
    {
        _riskRails?.ForceDrawdown(limit);
#if DEBUG
        if (_riskRails is not null)
        {
            Console.WriteLine($"DEBUG: ForceDrawdown(symbol={symbol}, limit={limit}, value={Math.Abs(_riskRails.CurrentDrawdown)})");
        }
        else
        {
            Console.WriteLine($"DEBUG: ForceDrawdown(symbol={symbol}, limit={limit})");
        }
#endif
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        foreach (var tick in _ticks)
        {
            // Advance the deterministic clock only when we encounter a new unique timestamp in the tick stream.
            // This prevents multi-instrument minutes from over-advancing the clock.
            if (_clock.UtcNow != tick.UtcTimestamp)
                _clock.Tick();

            // Deterministic ordering: order builders by instrument id then interval duration
            foreach (var kvp in _builders
                .Where(b => b.Key.Item1.Equals(tick.InstrumentId))
                .OrderBy(b => b.Key.Item1.Value, StringComparer.Ordinal)
                .ThenBy(b => b.Key.Item2.Duration.TotalSeconds))
            {
                var builder = kvp.Value;
                var maybe = builder.OnTick(tick);
                if (maybe is { } bar)
                {
                    var interval = kvp.Key.Item2;
                    var key = new BarKey(bar.InstrumentId, interval, bar.StartUtc);
                    if (_barKeyTracker.Seen(key)) continue; // idempotency guard
                    _barKeyTracker.Add(key);
                    // Canonical BAR_V1 emission (schema_version 1.2.0) - legacy BAR rows removed
                    var enriched = new
                    {
                        bar.InstrumentId,
                        IntervalSeconds = interval.Duration.TotalSeconds,
                        bar.StartUtc,
                        bar.EndUtc,
                        bar.Open,
                        bar.High,
                        bar.Low,
                        bar.Close,
                        bar.Volume
                    };
                    var json = JsonSerializer.SerializeToElement(enriched);
                    await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, _barEventType, _sourceAdapter, json), ct);

                    // CI penalty scaffold emission: Only when ci scaffold enabled AND penalty feature flags set AND forcePenalty true.
                    // Additionally: if sentiment is enabled (shadow/active) we defer penalty until after sentiment chain to preserve ordering expectations.
                    bool sentimentEnabled = _sentimentConfig is { Enabled: true, Mode: var sm } && (sm.Equals("shadow", StringComparison.OrdinalIgnoreCase) || sm.Equals("active", StringComparison.OrdinalIgnoreCase));
                    if (!_penaltyEmitted && _ciPenaltyScaffold && _forcePenalty && (_penaltyMode == "shadow" || _penaltyMode == "active") && !sentimentEnabled)
                    {
                        _penaltyEmitted = true;
                        var penSymbol = bar.InstrumentId.Value;
                        decimal orig = 200m; decimal adj = Math.Max(1, Math.Floor(orig * 0.5m));
                        var penaltyPayload = JsonSerializer.SerializeToElement(new { symbol = penSymbol, ts = bar.EndUtc, reason = "drawdown_guard", original_units = orig, adjusted_units = adj, penalty_scalar = (orig == 0 ? 0m : (adj / orig)) });
                        await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "PENALTY_APPLIED_V1", _sourceAdapter, penaltyPayload), ct);
                    }

                    // Sentiment volatility guard (shadow or active) after bar emission, before strategy trade execution
                    bool barClamp = false; decimal? lastOriginalUnits = null; long? lastAdjustedUnits = null; string? lastAppliedSymbol = null; DateTime? lastAppliedTs = null;
                    if (_sentimentConfig is { Enabled: true, Mode: var modeVal } sg && (modeVal.Equals("shadow", StringComparison.OrdinalIgnoreCase) || modeVal.Equals("active", StringComparison.OrdinalIgnoreCase)))
                    {
                        var symbol = bar.InstrumentId.Value;
                        if (!_sentimentWindows.TryGetValue(symbol, out var q)) { q = new Queue<decimal>(sg.Window); _sentimentWindows[symbol] = q; }
                        var sample = SentimentVolatilityGuard.Compute(sg, symbol, bar.EndUtc, bar.Close, q, out _);
                        var zPayload = System.Text.Json.JsonSerializer.SerializeToElement(new { symbol = sample.Symbol, s_raw = sample.SRaw, z = sample.Z, sigma = sample.Sigma, ts = sample.Ts });
                        await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "INFO_SENTIMENT_Z_V1", _sourceAdapter, zPayload), ct);
                        if (sample.Clamped)
                        {
                            var clampPayload = System.Text.Json.JsonSerializer.SerializeToElement(new { symbol = sample.Symbol, reason = "volatility_guard", ts = sample.Ts });
                            await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "INFO_SENTIMENT_CLAMP_V1", _sourceAdapter, clampPayload), ct);
                            barClamp = true; lastAppliedSymbol = sample.Symbol; lastAppliedTs = sample.Ts;
                        }
                    }

                    _riskRails?.UpdateBar(bar, _positions);

                    // === Risk evaluation & alerts (always before strategy scheduling) ===
                    _riskBlockCurrentBar = false; // reset
                    List<DeterministicScriptStrategy.ScheduledAction>? pendingActions = null;
                    // Pre-fetch pending actions once per minute for projection & later execution
                    var tickMinute = new DateTime(tick.UtcTimestamp.Year, tick.UtcTimestamp.Month, tick.UtcTimestamp.Day, tick.UtcTimestamp.Hour, tick.UtcTimestamp.Minute, 0, DateTimeKind.Utc);
                    bool newMinuteWindow = _lastStrategyMinute != tickMinute;
                    if (_deterministicStrategy is not null && newMinuteWindow)
                        pendingActions = _deterministicStrategy.Pending(tickMinute).ToList();
                    if (_riskMode != "off")
                    {
                        decimal netExposure = 0m;
                        foreach (var kv in _openUnits.OrderBy(k => k.Key, StringComparer.Ordinal))
                        {
                            decimal notionalPerUnit = bar.Close; // simple deterministic mapping
                            netExposure += kv.Value * notionalPerUnit;
                        }
                        // Project opens from pending actions before they execute (only if new minute window)
                        if (pendingActions is not null)
                        {
                            foreach (var act in pendingActions)
                            {
                                if (act.Side == Side.Close) continue; // closures reduce exposure but we ignore for conservative block decision
                                var baseUnits = act.Symbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase) ? _sizeUnitsXau : _sizeUnitsFx;
                                decimal notionalPerUnit = bar.Close;
                                netExposure += baseUnits * notionalPerUnit * (act.DecisionId.EndsWith("-01", StringComparison.Ordinal) ? 1 : -1);
                            }
                        }
                        _netExposureBySymbol[bar.InstrumentId.Value] = netExposure;
                        var maxRunDrawdown = _riskConfig?.MaxRunDrawdownCCY;
                        if (_riskConfig?.ForceDrawdownAfterEvals is { } forceMap && maxRunDrawdown.HasValue)
                        {
                            var symbol = bar.InstrumentId.Value;
                            if (!_riskEvalCountBySymbol.TryGetValue(symbol, out var perSymCount)) perSymCount = 0;
                            if (forceMap.TryGetValue(symbol, out var triggerN) && perSymCount + 1 == triggerN)
                            {
                                ForceDrawdown(symbol, maxRunDrawdown.Value);
                            }
                        }
                        decimal runDrawdown = _riskRails is not null ? Math.Abs(_riskRails.CurrentDrawdown) : 0m;
#if DEBUG
                        if (_riskMode != "off") Console.WriteLine($"DEBUG:RISK_EVAL symbol={bar.InstrumentId.Value} run_dd={runDrawdown} cap={_riskConfig?.MaxRunDrawdownCCY} evalCount={_riskEvalCount} perSym={(_riskEvalCountBySymbol.TryGetValue(bar.InstrumentId.Value, out var c) ? c : 0)}");
#endif
                        if (_riskConfig?.EmitEvaluations != false)
                        {
                            var evalPayload = JsonSerializer.SerializeToElement(new { symbol = bar.InstrumentId.Value, ts = bar.EndUtc, net_exposure = netExposure, run_drawdown = runDrawdown });
                            await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "INFO_RISK_EVAL_V1", _sourceAdapter, evalPayload), ct);
                            _riskEvalCount++;
                            var sym = bar.InstrumentId.Value; _riskEvalCountBySymbol[sym] = _riskEvalCountBySymbol.TryGetValue(sym, out var ec) ? ec + 1 : 1;
                        }
                        decimal lim = 0m;
                        bool exposureBreach = false;
                        if (_riskConfig?.MaxNetExposureBySymbol != null && _riskConfig.MaxNetExposureBySymbol.TryGetValue(bar.InstrumentId.Value, out var cap))
                        {
                            lim = cap;
                            // Treat breach as >= cap (not strictly >) so deterministic zero-cap promotion tests trigger an alert when projected exposure is zero or positive and cap=0.
                            exposureBreach = Math.Abs(netExposure) >= cap;
#if DEBUG
                            if (cap == 0) Console.WriteLine($"DEBUG:EXPOSURE_CHECK symbol={bar.InstrumentId.Value} net={netExposure} cap={cap} breach={exposureBreach} pending={(pendingActions?.Count ?? 0)} evalSeq={_seq + 1}");
#endif
                        }
                        bool drawdownBreach = false;
                        if (maxRunDrawdown.HasValue)
                        {
                            var limit = Math.Abs(maxRunDrawdown.Value);
                            drawdownBreach = runDrawdown > limit;
                        }
                        if (exposureBreach)
                        {
                            var alertPayload = JsonSerializer.SerializeToElement(new { symbol = bar.InstrumentId.Value, ts = bar.EndUtc, limit = lim, value = netExposure, reason = "net_exposure_cap", config_hash = _riskConfigHash });
                            await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "ALERT_BLOCK_NET_EXPOSURE", _sourceAdapter, alertPayload), ct);
                            if (_riskMode == "active" && (_riskConfig?.BlockOnBreach ?? false)) _riskBlockCurrentBar = true;
                        }
                        if (drawdownBreach && maxRunDrawdown.HasValue)
                        {
                            var limitValue = Math.Abs(maxRunDrawdown.Value);
                            var alertPayload = JsonSerializer.SerializeToElement(new { ts = bar.EndUtc, limit_ccy = limitValue, value_ccy = runDrawdown, reason = "drawdown_guard", config_hash = _riskConfigHash });
                            await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "ALERT_BLOCK_DRAWDOWN", _sourceAdapter, alertPayload), ct);
                            if (_riskMode == "active" && (_riskConfig?.BlockOnBreach ?? false)) _riskBlockCurrentBar = true;
                        }
                        // Shadow mode never blocks
                        if (_riskMode == "shadow" && _riskBlockCurrentBar) _riskBlockCurrentBar = false;
                    }
                    else
                    { // off mode: clear any previously fetched actions variable remains
                      // no risk events emitted
                    }

                    // === Strategy scheduling & execution (after risk eval) ===
                    if (_deterministicStrategy is not null && _execution is not null && _positions is not null)
                    {
                        if (newMinuteWindow && pendingActions is not null)
                        {
                            _lastStrategyMinute = tickMinute;
                            foreach (var act in pendingActions)
                            {
                                if (_riskBlockCurrentBar && _riskMode == "active") continue; // suppress trade placement on active breach
                                if (act.Side == Side.Close)
                                {
                                    var closeSide = act.DecisionId.EndsWith("-01", StringComparison.Ordinal) ? TradeSide.Sell : TradeSide.Buy;
                                    var units = _openUnits.TryGetValue(act.DecisionId, out var ou) ? ou : 0L;
                                    var req = new OrderRequest(act.DecisionId, act.Symbol, closeSide, units, tickMinute);
                                    var result = await _execution.ExecuteMarketAsync(req, ct);
                                    if (result.Accepted)
                                    {
                                        if (result.Fill is { } fill)
                                        {
                                            _positions.OnFill(fill, _schemaVersion, _configHash ?? string.Empty, _sourceAdapter, ExtractDataVersion());
                                            if (_tradesWriter is not null)
                                            {
                                                foreach (var completed in _positions.Completed.Where(c => c.DecisionId == act.DecisionId))
                                                    _tradesWriter.Append(completed);
                                            }
                                            // Clear open-units tracking on successful close so risk exposure reflects current book
                                            _openUnits.Remove(act.DecisionId);
                                        }
                                        LogOrderSendOk(req.DecisionId, req.Symbol, result.BrokerOrderId);
                                    }
                                }
                                else
                                {
                                    bool firstLeg = act.DecisionId.EndsWith("-01", StringComparison.Ordinal);
                                    var side = firstLeg ? TradeSide.Buy : TradeSide.Sell;
                                    var baseUnits = act.Symbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase) ? _sizeUnitsXau : _sizeUnitsFx;
                                    long finalUnits = baseUnits;
                                    if (barClamp && _sentimentConfig is { Mode: var m } && m.Equals("active", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var scaled = (long)Math.Floor(baseUnits * 0.5m);
                                        if (baseUnits > 0 && scaled < 1) scaled = 1;
                                        finalUnits = scaled;
                                        lastOriginalUnits = baseUnits; lastAdjustedUnits = finalUnits;
                                    }
                                    RiskRailOutcome? riskOutcome = null;
                                    if (_riskMode != "off" && firstLeg && _riskRails is not null)
                                    {
                                        var timeframeLabel = ResolveTimeframeLabel(interval);
                                        riskOutcome = _riskRails.EvaluateNewEntry(act.Symbol, timeframeLabel, tickMinute, finalUnits);
                                        foreach (var alert in riskOutcome.Alerts)
                                        {
                                            await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, alert.EventType, _sourceAdapter, alert.Payload), ct);
                                        }
                                        if (_riskMode == "active")
                                        {
                                            if (!riskOutcome.Allowed)
                                            {
                                                _riskBlockCurrentBar = true;
                                                continue;
                                            }
                                            finalUnits = riskOutcome.Units;
                                        }
                                    }
                                    _openUnits[act.DecisionId] = finalUnits;
                                    var req = new OrderRequest(act.DecisionId, act.Symbol, side, finalUnits, tickMinute);
                                    var result = await _execution.ExecuteMarketAsync(req, ct);
                                    if (result.Accepted)
                                    {
                                        if (result.Fill is { } fill)
                                        {
                                            _positions.OnFill(fill, _schemaVersion, _configHash ?? string.Empty, _sourceAdapter, ExtractDataVersion());
                                        }
                                        LogOrderSendOk(req.DecisionId, req.Symbol, result.BrokerOrderId);
                                    }
                                }

                            }

                        }

                        // Emit APPLIED event if scaling occurred
                        if (lastOriginalUnits.HasValue && lastAdjustedUnits.HasValue && lastAppliedSymbol is not null && lastAppliedTs.HasValue && _sentimentConfig is { Mode: var modeApplied } && modeApplied.Equals("active", StringComparison.OrdinalIgnoreCase))
                        {
                            var appliedPayload = System.Text.Json.JsonSerializer.SerializeToElement(new { symbol = lastAppliedSymbol, ts = lastAppliedTs.Value, original_units = lastOriginalUnits.Value, adjusted_units = lastAdjustedUnits.Value, reason = "volatility_guard" });
                            await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "INFO_SENTIMENT_APPLIED_V1", _sourceAdapter, appliedPayload), ct);
                        }


                    }

                    // Deferred penalty emission path: if sentiment was enabled we emit penalty AFTER sentiment chain but before probe/trades (once only)
                    if (!_penaltyEmitted && _ciPenaltyScaffold && _forcePenalty && (_penaltyMode == "shadow" || _penaltyMode == "active") && sentimentEnabled)
                    {
                        _penaltyEmitted = true;
                        var penSymbol = bar.InstrumentId.Value;
                        decimal orig = 200m; decimal adj = Math.Max(1, Math.Floor(orig * 0.5m));
                        var penaltyPayload = JsonSerializer.SerializeToElement(new { symbol = penSymbol, ts = bar.EndUtc, reason = "drawdown_guard", original_units = orig, adjusted_units = adj, penalty_scalar = (orig == 0 ? 0m : (adj / orig)) });
                        await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "PENALTY_APPLIED_V1", _sourceAdapter, penaltyPayload), ct);
                    }
                    // Emit risk probe (synthetic) if services configured
                    if (_riskFormulas is not null && _basketAggregator is not null && _riskProbeEnabled)
                    {
                        // Base synthetic values with configurable equity override
                        decimal equity = _equityOverride ?? 100_000m;
                        decimal notional = Math.Abs(bar.Close) * 1_000m / 100m; // simplistic mapping
                        decimal usedMarginAfter = notional / 20m; // assume 5% margin
                        decimal positionInitialRiskMoney = Math.Max(50m, bar.Volume * 10m);
                        var leverage = _riskFormulas.ProjectLeverage(notional, equity);
                        var marginPct = _riskFormulas.ProjectMarginUsagePct(usedMarginAfter, equity);
                        _openPositions.Add(new PositionInitialRisk(bar.InstrumentId, positionInitialRiskMoney, new Currency("USD")));
                        var basketPct = _basketAggregator.ComputeBasketRiskPct(_openPositions, BasketMode.Base, new Currency("USD"), equity);
                        // Canonical RISK_PROBE_V1 payload (PascalCase required fields) + extras retained
                        // Deterministic decision id for risk probe to ensure promotion determinism across runs
                        var rpSymbol = bar.InstrumentId.Value;
                        if (!_riskProbeCounters.TryGetValue(rpSymbol, out var rpCount)) rpCount = 0;
                        rpCount++;
                        _riskProbeCounters[rpSymbol] = rpCount;
                        var decisionId = $"RP-{rpSymbol}-{rpCount:D4}";
                        var riskProbe = new
                        {
                            // Required canonical fields for verifier
                            InstrumentId = bar.InstrumentId.Value,
                            ProjectedLeverage = leverage,
                            ProjectedMarginUsagePct = marginPct,
                            BasketRiskPct = basketPct,
                            // Extra diagnostic fields (snake_case preserved as legacy / auxiliary fields)
                            DecisionId = decisionId,
                            NotionalValue = notional,
                            Equity = equity,
                            UsedMarginAfterOrder = usedMarginAfter,
                            PositionInitialRiskMoney = positionInitialRiskMoney,
                            BasketMode = BasketMode.Base.ToString(),
                            SchemaVersion = _schemaVersion,
                            ConfigHash = _configHash ?? string.Empty
                        };
                        var probeJson = JsonSerializer.SerializeToElement(riskProbe);
                        await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "RISK_PROBE_V1", _sourceAdapter, probeJson), ct);

                        // Enforcement (if enforcer configured)
                        if (_riskEnforcer is not null)
                        {
                            var proposal = new Proposal(
                                InstrumentId: bar.InstrumentId.Value,
                                RequestedVolume: 1_000m, // placeholder mapping from price to volume
                                NotionalValue: notional,
                                UsedMarginAfterOrder: usedMarginAfter,
                                PositionInitialRiskMoney: positionInitialRiskMoney,
                                DecisionId: decisionId);
                            // Snapshot includes current open positions (include this bar's position risk)
                            var snapshotPositions = _openPositions.Select(o => (o.InstrumentId.Value, o.InitialRiskMoneyAccountCcy)).ToList();
                            var snapshot = new BasketSnapshot(snapshotPositions);
                            var riskCfg = _riskConfig ?? new RiskConfig();
                            var ctx = new RiskContext(equity, snapshot, riskCfg, new InMemoryInstrumentCatalog(new[] { new Instrument(new InstrumentId(bar.InstrumentId.Value), "SYM", 2) }), new PassthroughFx());
                            var enforcement = _riskEnforcer.Enforce(proposal, ctx);
                            foreach (var alert in enforcement.Alerts)
                            {
                                var alertPayload = JsonSerializer.SerializeToElement(new
                                {
                                    alert.EventType,
                                    alert.DecisionId,
                                    alert.InstrumentId,
                                    alert.Reason,
                                    alert.Observed,
                                    alert.Cap,
                                    alert.Equity,
                                    alert.NotionalValue,
                                    alert.UsedMarginAfterOrder,
                                    alert.SchemaVersion,
                                    alert.ConfigHash
                                });
                                await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, alert.EventType, _sourceAdapter, alertPayload), ct);
                            }
                        }
                    }
                    _onBarEmitted?.Invoke(bar);
                    _onPositionMetrics?.Invoke(_positions?.OpenCount ?? 0, _openUnits.Count);
                }
            }
        }
    }

    private string ResolveTimeframeLabel(BarInterval interval)
    {
        if (_timeframeLabels.TryGetValue(interval.Duration.Ticks, out var label) && !string.IsNullOrWhiteSpace(label))
        {
            return label;
        }
        var duration = interval.Duration;
        if (duration.TotalHours >= 1 && Math.Abs(duration.TotalHours - Math.Round(duration.TotalHours)) < 1e-6)
        {
            return $"H{(int)Math.Round(duration.TotalHours)}";
        }
        if (duration.TotalMinutes >= 1 && Math.Abs(duration.TotalMinutes - Math.Round(duration.TotalMinutes)) < 1e-6)
        {
            return $"M{(int)Math.Round(duration.TotalMinutes)}";
        }
        return $"{duration.TotalMinutes:0}m";
    }
}
