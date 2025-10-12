using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace TiYf.Engine.Core;

// Time abstraction (port)
public interface IClock
{
    DateTime UtcNow { get; }
    // For deterministic simulation advance to next timestamp in sequence or add a step.
    DateTime Tick();
}

// Deterministic sequence-based clock for replay / tests.
public sealed class DeterministicSequenceClock : IClock
{
    private readonly IReadOnlyList<DateTime> _sequence;
    private int _index = -1;

    public DeterministicSequenceClock(IEnumerable<DateTime> sequence)
    {
        _sequence = sequence.Select(dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToList();
        if (_sequence.Count == 0) throw new ArgumentException("Sequence must have at least one timestamp", nameof(sequence));
    }

    public DateTime UtcNow => _index >= 0 && _index < _sequence.Count ? _sequence[_index] : _sequence[0];

    public DateTime Tick()
    {
        if (_index + 1 >= _sequence.Count)
            return _sequence[^1]; // Clamp at last
        _index++;
        return _sequence[_index];
    }
}

// Production system clock (non-deterministic, used only outside strict replay contexts)
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime Tick() => UtcNow; // Tick returns current time
}

// Instrument Model
public readonly record struct InstrumentId(string Value)
{
    public override string ToString() => Value;
}

public sealed record Instrument(InstrumentId Id, string Symbol, int PriceDecimals);

public interface IInstrumentCatalog
{
    bool TryGet(InstrumentId id, out Instrument instrument);
    IEnumerable<Instrument> All();
}

public sealed class InMemoryInstrumentCatalog : IInstrumentCatalog
{
    private readonly Dictionary<InstrumentId, Instrument> _map;
    public InMemoryInstrumentCatalog(IEnumerable<Instrument> instruments)
    {
        _map = instruments.ToDictionary(i => i.Id, i => i);
    }
    public bool TryGet(InstrumentId id, out Instrument instrument) => _map.TryGetValue(id, out instrument!);
    public IEnumerable<Instrument> All() => _map.Values;
}

// Market Data Tick
public sealed record PriceTick
{
    public InstrumentId InstrumentId { get; }
    public DateTime UtcTimestamp { get; }
    public decimal Price { get; }
    public decimal Volume { get; }

    public PriceTick(InstrumentId instrumentId, DateTime utcTimestamp, decimal price, decimal volume)
    {
        if (utcTimestamp.Kind != DateTimeKind.Utc)
            throw new ArgumentException("PriceTick.UtcTimestamp must be DateTimeKind.Utc", nameof(utcTimestamp));
        InstrumentId = instrumentId;
        UtcTimestamp = utcTimestamp;
        Price = price;
        Volume = volume;
    }
}

// Bar representation (OHLCV) for fixed interval (e.g., 1 minute)
public sealed record Bar(
    InstrumentId InstrumentId,
    DateTime StartUtc,
    DateTime EndUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public interface IBarBuilder
{
    // Feed incoming tick; returns a completed bar if this tick closed a bar; null otherwise.
    Bar? OnTick(PriceTick tick);
    void Reset();
}

public readonly record struct BarInterval(TimeSpan Duration)
{
    public static readonly BarInterval OneMinute = new(TimeSpan.FromMinutes(1));
    public static readonly BarInterval OneHour = new(TimeSpan.FromHours(1));
    public static readonly BarInterval OneDay = new(TimeSpan.FromDays(1));
    public DateTime Align(DateTime ts)
    {
        if (Duration == TimeSpan.FromDays(1))
            return new DateTime(ts.Year, ts.Month, ts.Day, 0, 0, 0, DateTimeKind.Utc);
        if (Duration == TimeSpan.FromHours(1))
            return new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, 0, 0, DateTimeKind.Utc);
        if (Duration == TimeSpan.FromMinutes(1))
            return new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0, DateTimeKind.Utc);
        // Generic alignment: floor by ticks
        var ticks = Duration.Ticks;
        var alignedTicks = (ts.Ticks / ticks) * ticks;
        return new DateTime(alignedTicks, DateTimeKind.Utc);
    }
    public DateTime NextStart(DateTime aligned) => aligned + Duration;
}

public sealed class IntervalBarBuilder : IBarBuilder
{
    private readonly BarInterval _interval;
    private InstrumentId? _instrument;
    private DateTime _currentStart;
    private decimal _open, _high, _low, _close, _volume;
    private bool _hasActive;

    public IntervalBarBuilder(BarInterval interval) => _interval = interval;

    public Bar? OnTick(PriceTick tick)
    {
        var ts = DateTime.SpecifyKind(tick.UtcTimestamp, DateTimeKind.Utc);
        var bucketStart = _interval.Align(ts);
        if (!_hasActive)
        {
            StartNew(bucketStart, tick);
            return null;
        }
        if (bucketStart != _currentStart)
        {
            var finished = BuildCurrent(_interval.NextStart(_currentStart));
            StartNew(bucketStart, tick);
            return finished;
        }
        _high = Math.Max(_high, tick.Price);
        _low = Math.Min(_low, tick.Price);
        _close = tick.Price;
        _volume += tick.Volume;
        return null;
    }

    public void Reset() => _hasActive = false;

    private void StartNew(DateTime bucketStart, PriceTick tick)
    {
        _instrument = tick.InstrumentId;
        _currentStart = bucketStart;
        _open = _high = _low = _close = tick.Price;
        _volume = tick.Volume;
        _hasActive = true;
    }

    private Bar BuildCurrent(DateTime bucketEnd)
        => new Bar(_instrument!.Value, _currentStart, bucketEnd, _open, _high, _low, _close, _volume);
}

// Bar uniqueness key for idempotency across restarts / replays
public readonly record struct BarKey(InstrumentId InstrumentId, BarInterval Interval, DateTime OpenTimeUtc)
{
    public override string ToString() => $"{InstrumentId.Value}|{Interval.Duration.TotalSeconds}|{OpenTimeUtc:O}";
}

public interface IBarKeyTracker
{
    bool Seen(BarKey key); // returns true if already processed
    void Add(BarKey key);
    IEnumerable<BarKey> Snapshot();
}

public sealed class InMemoryBarKeyTracker : IBarKeyTracker
{
    private readonly HashSet<BarKey> _set;
    public InMemoryBarKeyTracker() : this(Enumerable.Empty<BarKey>()) { }
    public InMemoryBarKeyTracker(IEnumerable<BarKey> seed)
    {
        _set = new HashSet<BarKey>(seed);
    }
    public bool Seen(BarKey key) => _set.Contains(key);
    public void Add(BarKey key) => _set.Add(key);
    public IEnumerable<BarKey> Snapshot() => _set.ToArray();
}

public static class BarKeyTrackerPersistence
{
    private sealed record SnapshotFile(string SchemaVersion, string EngineInstanceId, List<SnapshotFile.SnapshotBar> Bars)
    {
        public sealed record SnapshotBar(string InstrumentId, double IntervalSeconds, DateTime OpenTimeUtc);
    }

    public static InMemoryBarKeyTracker Load(string path)
    {
        if (!File.Exists(path)) return new InMemoryBarKeyTracker();
        var json = File.ReadAllText(path);
        var snap = JsonSerializer.Deserialize<SnapshotFile>(json) ?? throw new InvalidOperationException("Invalid bar key snapshot");
        var bars = snap.Bars.Select(b => new BarKey(new InstrumentId(b.InstrumentId), new BarInterval(TimeSpan.FromSeconds(b.IntervalSeconds)), b.OpenTimeUtc));
        return new InMemoryBarKeyTracker(bars);
    }

    public static void Save(string path, InMemoryBarKeyTracker tracker, string schemaVersion, string engineInstanceId)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var snap = new SnapshotFile(schemaVersion, engineInstanceId, tracker.Snapshot()
            .Select(k => new SnapshotFile.SnapshotBar(k.InstrumentId.Value, k.Interval.Duration.TotalSeconds, k.OpenTimeUtc))
            .ToList());
        var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = false });
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}

// Risk rails skeleton (placeholder)
public interface IRiskEvaluator
{
    RiskDecision Evaluate(object orderLike);
}

public enum RiskDecision { Accept, Reject, Review }

public sealed class NoOpRiskEvaluator : IRiskEvaluator
{
    public RiskDecision Evaluate(object orderLike) => RiskDecision.Accept;
}

// ==== Risk Packet B Additions ====
public readonly record struct Currency(string Code)
{
    public override string ToString() => Code;
}

public interface IRiskFormulas
{
    decimal ProjectLeverage(decimal notional, decimal equity);
    decimal ProjectMarginUsagePct(decimal usedMarginAfterOrder, decimal equity);
}

public sealed class RiskFormulas : IRiskFormulas
{
    public decimal ProjectLeverage(decimal notional, decimal equity)
    {
        if (equity == 0) return 0;
        return Decimal.Round(notional / equity, 6, MidpointRounding.AwayFromZero);
    }
    public decimal ProjectMarginUsagePct(decimal usedMarginAfterOrder, decimal equity)
    {
        if (equity == 0) return 0;
        return Decimal.Round(100m * usedMarginAfterOrder / equity, 6, MidpointRounding.AwayFromZero);
    }
}

public enum BasketMode { Base, Quote, UsdProxy, InstrumentBucket }

public sealed record PositionInitialRisk(InstrumentId InstrumentId, decimal InitialRiskMoneyAccountCcy, Currency PositionCcy);

public interface IBasketRiskAggregator
{
    decimal ComputeBasketRiskPct(IEnumerable<PositionInitialRisk> positions, BasketMode mode, Currency accountCcy, decimal equity, IReadOnlyDictionary<string, string>? instrumentBuckets = null, Func<Currency, Currency, decimal>? fx = null);
}

public sealed class BasketRiskAggregator : IBasketRiskAggregator
{
    public decimal ComputeBasketRiskPct(IEnumerable<PositionInitialRisk> positions, BasketMode mode, Currency accountCcy, decimal equity, IReadOnlyDictionary<string, string>? instrumentBuckets = null, Func<Currency, Currency, decimal>? fx = null)
    {
        fx ??= static (_, _) => 1m; // stub
        if (equity == 0) return 0;
        // Grouping key
        string Key(PositionInitialRisk p) => mode switch
        {
            BasketMode.Base => p.InstrumentId.Value.Length >= 6 ? p.InstrumentId.Value[..3] : p.InstrumentId.Value,
            BasketMode.Quote => p.InstrumentId.Value.Length >= 6 ? p.InstrumentId.Value.Substring(3, 3) : p.InstrumentId.Value,
            BasketMode.UsdProxy => "USD", // collapse to USD proxy bucket
            BasketMode.InstrumentBucket => instrumentBuckets != null && instrumentBuckets.TryGetValue(p.InstrumentId.Value, out var bucket) ? bucket : "__default__",
            _ => "__other__"
        };
        var grouped = positions.GroupBy(p => Key(p));
        decimal maxPct = 0;
        foreach (var g in grouped)
        {
            var sum = g.Sum(p => p.InitialRiskMoneyAccountCcy * fx(p.PositionCcy, accountCcy));
            var pct = sum / equity * 100m;
            if (pct > maxPct) maxPct = pct;
        }
        return Decimal.Round(maxPct, 6, MidpointRounding.AwayFromZero);
    }
}

// Journaling Port
public interface IJournalWriter : IAsyncDisposable
{
    Task AppendAsync(JournalEvent evt, CancellationToken ct = default);
}

public sealed record JournalEvent(ulong Sequence, DateTime UtcTimestamp, string EventType, JsonElement Payload)
{
    public string ToCsvLine() => string.Join(',', new[]
    {
        Sequence.ToString(),
        UtcTimestamp.ToString("O"),
        Escape(EventType),
        Escape(JsonSerializer.Serialize(Payload))
    });

    private static string Escape(string v)
    {
        if (v.Contains(',') || v.Contains('"'))
        {
            return '"' + v.Replace("\"", "\"\"") + '"';
        }
        return v;
    }
}

// Config hashing utility (deterministic) - stable canonical JSON hashing
public static class ConfigHash
{
    public static string Compute(byte[] utf8Json)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(utf8Json);
        return Convert.ToHexString(hash); // Uppercase hex
    }
}
