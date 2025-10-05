using System.Text.Json;
using TiYf.Engine.Core;

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
	private readonly List<PositionInitialRisk> _openPositions = new();
	private readonly string? _configHash; // pass-through for risk probe journaling
	private readonly string _schemaVersion;

	public EngineLoop(IClock clock, Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder> builders, IBarKeyTracker tracker, IJournalWriter journal, ITickSource ticks, string barEventType, Action? onBarEmitted = null, IRiskFormulas? riskFormulas = null, IBasketRiskAggregator? basketAggregator = null, string? configHash = null, string schemaVersion = TiYf.Engine.Core.Infrastructure.Schema.Version)
	{
		_clock = clock; _builders = builders; _barKeyTracker = tracker; _journal = journal; _ticks = ticks; _barEventType = barEventType; _seq = 0UL; _onBarEmitted = onBarEmitted; _riskFormulas = riskFormulas; _basketAggregator = basketAggregator; _configHash = configHash; _schemaVersion = schemaVersion;
	}

	public async Task RunAsync(CancellationToken ct = default)
	{
		foreach (var tick in _ticks)
		{
			_clock.Tick();
			foreach (var kvp in _builders.Where(b => b.Key.Item1.Equals(tick.InstrumentId)))
			{
				var builder = kvp.Value;
				var maybe = builder.OnTick(tick);
				if (maybe is { } bar)
				{
					var interval = kvp.Key.Item2;
					var key = new BarKey(bar.InstrumentId, interval, bar.StartUtc);
					if (_barKeyTracker.Seen(key)) continue; // idempotency guard
					_barKeyTracker.Add(key);
					// Canonical BAR_V1 emission (schema_version 1.1.0) - legacy BAR rows removed
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
					// Emit risk probe (synthetic) if services configured
					if (_riskFormulas is not null && _basketAggregator is not null)
					{
						// Synthetic assumptions (placeholder heuristics)
						decimal equity = 100_000m;
						decimal notional = Math.Abs(bar.Close) * 1_000m / 100m; // simplistic scaling
						decimal usedMarginAfter = notional / 20m; // assume 5% margin
						decimal positionInitialRiskMoney = Math.Max(50m, bar.Volume * 10m);
						var leverage = _riskFormulas.ProjectLeverage(notional, equity);
						var marginPct = _riskFormulas.ProjectMarginUsagePct(usedMarginAfter, equity);
						_openPositions.Add(new PositionInitialRisk(bar.InstrumentId, positionInitialRiskMoney, new Currency("USD")));
						var basketPct = _basketAggregator.ComputeBasketRiskPct(_openPositions, BasketMode.Base, new Currency("USD"), equity);
						var probe = new {
							decision_id = Guid.NewGuid().ToString("N"),
							instrumentId = bar.InstrumentId.Value,
							notional_value = notional,
							equity,
							used_margin_after_order = usedMarginAfter,
							projected_leverage = leverage,
							projected_margin_usage_pct = marginPct,
							position_initial_risk_money = positionInitialRiskMoney,
							basket_risk_pct = basketPct,
							basket_mode = BasketMode.Base.ToString(),
							schema_version = _schemaVersion,
							config_hash = _configHash ?? string.Empty
						};
						var probeJson = JsonSerializer.SerializeToElement(probe);
						await _journal.AppendAsync(new JournalEvent(++_seq, bar.EndUtc, "RISK_PROBE_V1", probeJson), ct);
					}
					_onBarEmitted?.Invoke();
				}
			}
		}
	}
}
