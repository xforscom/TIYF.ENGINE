using System.Text.Json;
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
			.Select(line => {
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
	private readonly Action? _onBarEmitted;
	private readonly IRiskFormulas? _riskFormulas;
	private readonly IBasketRiskAggregator? _basketAggregator;
	private readonly IRiskEnforcer? _riskEnforcer; // new enforcement dependency
	private readonly List<PositionInitialRisk> _openPositions = new();
	private readonly string? _configHash; // pass-through for risk probe journaling
	private readonly string _schemaVersion;
	private readonly RiskConfig? _riskConfig;
	private readonly decimal? _equityOverride;
    private readonly DeterministicScriptStrategy? _deterministicStrategy; // optional strategy for M0
	private readonly IExecutionAdapter? _execution;
	private readonly PositionTracker? _positions;
	private readonly TradesJournalWriter? _tradesWriter;
	private readonly string? _dataVersion;
	private readonly Dictionary<string,long> _openUnits = new(); // decisionId -> units
	private readonly SentimentGuardConfig? _sentimentConfig;
	private readonly Dictionary<string,int> _decisionCounters = new(StringComparer.Ordinal);
	private readonly Dictionary<string,int> _riskProbeCounters = new(StringComparer.Ordinal); // per-instrument deterministic counter for risk probe DecisionId
	private readonly Dictionary<string,Queue<decimal>> _sentimentWindows = new(StringComparer.Ordinal);
	private DateTime? _lastStrategyMinute; // to avoid emitting strategy actions multiple times per minute when multiple instrument ticks share the same minute
    private readonly bool _riskProbeEnabled = true; // feature flag

	private string? ExtractDataVersion() => _dataVersion;

	public EngineLoop(IClock clock, Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder> builders, IBarKeyTracker tracker, IJournalWriter journal, ITickSource ticks, string barEventType, Action? onBarEmitted = null, IRiskFormulas? riskFormulas = null, IBasketRiskAggregator? basketAggregator = null, string? configHash = null, string schemaVersion = TiYf.Engine.Core.Infrastructure.Schema.Version, IRiskEnforcer? riskEnforcer = null, RiskConfig? riskConfig = null, decimal? equityOverride = null, DeterministicScriptStrategy? deterministicStrategy = null, IExecutionAdapter? execution = null, PositionTracker? positions = null, TradesJournalWriter? tradesWriter = null, string? dataVersion = null, long sizeUnitsFx = 1000, long sizeUnitsXau = 1, bool riskProbeEnabled = true, SentimentGuardConfig? sentimentConfig = null)
	{
		_clock = clock; _builders = builders; _barKeyTracker = tracker; _journal = journal; _ticks = ticks; _barEventType = barEventType; _seq = (journal is FileJournalWriter fj ? fj.NextSequence : 1UL) - 1UL; _onBarEmitted = onBarEmitted; _riskFormulas = riskFormulas; _basketAggregator = basketAggregator; _configHash = configHash; _schemaVersion = schemaVersion; _riskEnforcer = riskEnforcer; _riskConfig = riskConfig; _equityOverride = equityOverride; _deterministicStrategy = deterministicStrategy; _execution = execution; _positions = positions; _tradesWriter = tradesWriter; _dataVersion = dataVersion; _riskProbeEnabled = riskProbeEnabled; _sentimentConfig = sentimentConfig;
		_sizeUnitsFx = sizeUnitsFx; _sizeUnitsXau = sizeUnitsXau;
	}
	private readonly long _sizeUnitsFx; private readonly long _sizeUnitsXau;

	public async Task RunAsync(CancellationToken ct = default)
	{
			foreach (var tick in _ticks)
			{
				// Advance the deterministic clock only when we encounter a new unique timestamp in the tick stream.
				// This prevents multi-instrument minutes from over-advancing the clock.
				if (_clock.UtcNow != tick.UtcTimestamp)
					_clock.Tick();

				// Strategy scheduling -> real order execution path (run once per unique minute)
				if (_deterministicStrategy is not null && _execution is not null && _positions is not null)
				{
					var tickMinute = new DateTime(tick.UtcTimestamp.Year, tick.UtcTimestamp.Month, tick.UtcTimestamp.Day, tick.UtcTimestamp.Hour, tick.UtcTimestamp.Minute, 0, DateTimeKind.Utc);
					if (_lastStrategyMinute != tickMinute)
					{
						_lastStrategyMinute = tickMinute;
						foreach (var act in _deterministicStrategy.Pending(tickMinute))
						{
							if (act.Side == Side.Close)
							{
								// Close = opposite market order
								var closeSide = act.Symbol switch { _ => TradeSide.Buy }; // will be set below
								// Determine original side from decision id pattern (01 => first leg BUY, 02 => second leg SELL)
								bool firstLeg = act.DecisionId.EndsWith("-01", StringComparison.Ordinal);
								var entrySide = firstLeg ? TradeSide.Buy : TradeSide.Sell;
								closeSide = entrySide == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
								// Units: match open units stored
								var units = _openUnits.TryGetValue(act.DecisionId, out var ou) ? ou : 0L;
								var req = new OrderRequest(act.DecisionId, act.Symbol, closeSide, units, tickMinute);
								var result = await _execution.ExecuteMarketAsync(req, ct);
								if (result.Accepted && result.Fill is { } fill)
								{
									_positions.OnFill(fill, _schemaVersion, _configHash ?? string.Empty, ExtractDataVersion());
									// if trade closed produce into trades writer
									if (_tradesWriter is not null)
									{
										foreach (var completed in _positions.Completed.Where(c=>c.DecisionId==act.DecisionId))
											_tradesWriter.Append(completed);
									}
								}
							}
							else
							{
								// Open leg: determine side (BUY for -01 first emission, SELL for -02 first emission)
								bool firstLeg = act.DecisionId.EndsWith("-01", StringComparison.Ordinal);
								var side = firstLeg ? TradeSide.Buy : TradeSide.Sell;
								// Units selection heuristic: XAUUSD uses 1, others 1000 (will later read from config params)
								var units = act.Symbol.Equals("XAUUSD", StringComparison.OrdinalIgnoreCase) ? _sizeUnitsXau : _sizeUnitsFx;
								_openUnits[act.DecisionId] = units;
								var req = new OrderRequest(act.DecisionId, act.Symbol, side, units, tickMinute);
								var result = await _execution.ExecuteMarketAsync(req, ct);
								if (result.Accepted && result.Fill is { } fill)
									_positions.OnFill(fill, _schemaVersion, _configHash ?? string.Empty, ExtractDataVersion());
							}
						}
					}
				}
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
					await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, _barEventType, json), ct);

					// Sentiment volatility guard (shadow only) after bar emission
					if (_sentimentConfig is { Enabled: true } sg)
					{
						var symbol = bar.InstrumentId.Value;
						if (!_sentimentWindows.TryGetValue(symbol, out var q)) { q = new Queue<decimal>(sg.Window); _sentimentWindows[symbol] = q; }
						var sample = SentimentVolatilityGuard.Compute(sg, symbol, bar.EndUtc, bar.Close, q, out _);
						var zPayload = System.Text.Json.JsonSerializer.SerializeToElement(new { symbol = sample.Symbol, s_raw = sample.SRaw, z = sample.Z, sigma = sample.Sigma, ts = sample.Ts });
						await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "INFO_SENTIMENT_Z_V1", zPayload), ct);
						if (sample.Clamped)
						{
							var clampPayload = System.Text.Json.JsonSerializer.SerializeToElement(new { symbol = sample.Symbol, reason = "volatility_guard", ts = sample.Ts });
							await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "INFO_SENTIMENT_CLAMP_V1", clampPayload), ct);
						}
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
						var riskProbe = new {
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
						await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "RISK_PROBE_V1", probeJson), ct);

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
							var ctx = new RiskContext(equity, snapshot, riskCfg, new InMemoryInstrumentCatalog(new[]{ new Instrument(new InstrumentId(bar.InstrumentId.Value), "SYM", 2)}), new PassthroughFx());
							var enforcement = _riskEnforcer.Enforce(proposal, ctx);
							foreach (var alert in enforcement.Alerts)
							{
								var alertPayload = JsonSerializer.SerializeToElement(new {
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
								await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, alert.EventType, alertPayload), ct);
							}
						}
					}
					_onBarEmitted?.Invoke();
				}
			}
		}
	}
}
