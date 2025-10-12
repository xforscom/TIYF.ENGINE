using System.Text;
using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Sim;

namespace TiYf.Engine.Tests;

public class MultiInstrumentTests
{
    private static List<PriceTick> InterleavedTicks()
    {
        var instA = new InstrumentId("EURUSD");
        var instB = new InstrumentId("GBPUSD");
        var baseTime = new DateTime(2025, 10, 5, 10, 0, 0, DateTimeKind.Utc);
        return new List<PriceTick>
        {
            new(instA, baseTime.AddSeconds(5), 1.1000m, 1000),
            new(instB, baseTime.AddSeconds(7), 1.2500m, 2000),
            new(instA, baseTime.AddSeconds(35), 1.1010m, 800),
            new(instB, baseTime.AddSeconds(50), 1.2490m, 600),
            new(instA, baseTime.AddMinutes(1), 1.1020m, 500), // boundary for A
            new(instB, baseTime.AddMinutes(1), 1.2510m, 700), // boundary for B
        };
    }

    private static async Task<(string eventsPath, string snapshot)> RunOnceAsync(List<PriceTick> ticks)
    {
        var runId = Guid.NewGuid().ToString("N");
        var journalRoot = Path.Combine(Path.GetTempPath(), "journals-tests");
        Directory.CreateDirectory(journalRoot);
        var clock = new DeterministicSequenceClock(ticks.Select(t => t.UtcTimestamp));
        var builders = new Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder>
        {
            [(new InstrumentId("EURUSD"), BarInterval.OneMinute)] = new IntervalBarBuilder(BarInterval.OneMinute),
            [(new InstrumentId("GBPUSD"), BarInterval.OneMinute)] = new IntervalBarBuilder(BarInterval.OneMinute),
        };
        var tracker = new InMemoryBarKeyTracker();
        var journal = new TestJournalWriter();
        var loop = new EngineLoop(clock, builders, tracker, journal, new EnumerableTickSource(ticks), "BAR_V1");
        await loop.RunAsync();
        var snapshot = string.Join('\n', journal.Lines);
        return ("", snapshot);
    }

    [Fact]
    public async Task MultiInstrumentDeterminism_ShouldProduceBitExactEvents()
    {
        var ticks = InterleavedTicks();
        var r1 = await RunOnceAsync(ticks);
        var r2 = await RunOnceAsync(ticks); // same ticks
        Assert.Equal(r1.snapshot, r2.snapshot); // bit-exact
    }

    [Fact]
    public async Task RestartContinuity_ShouldNotDuplicateBars()
    {
        var ticks = InterleavedTicks();
        // First half
        var firstHalf = ticks.Take(3).ToList();
        var secondHalf = ticks.Skip(3).ToList();
        var clock1 = new DeterministicSequenceClock(firstHalf.Select(t => t.UtcTimestamp));
        var builders1 = new Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder>
        {
            [(new InstrumentId("EURUSD"), BarInterval.OneMinute)] = new IntervalBarBuilder(BarInterval.OneMinute),
            [(new InstrumentId("GBPUSD"), BarInterval.OneMinute)] = new IntervalBarBuilder(BarInterval.OneMinute),
        };
        var tracker = new InMemoryBarKeyTracker();
        var journal1 = new TestJournalWriter();
        var loop1 = new EngineLoop(clock1, builders1, tracker, journal1, new EnumerableTickSource(firstHalf), "BAR_V1");
        await loop1.RunAsync();
        var firstSnapshotCount = journal1.Lines.Count;
        // Resume with remaining ticks using same tracker
        var clock2 = new DeterministicSequenceClock(secondHalf.Select(t => t.UtcTimestamp));
        var builders2 = new Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder>
        {
            [(new InstrumentId("EURUSD"), BarInterval.OneMinute)] = new IntervalBarBuilder(BarInterval.OneMinute),
            [(new InstrumentId("GBPUSD"), BarInterval.OneMinute)] = new IntervalBarBuilder(BarInterval.OneMinute),
        };
        var journal2 = new TestJournalWriter();
        var loop2 = new EngineLoop(clock2, builders2, tracker, journal2, new EnumerableTickSource(secondHalf), "BAR_V1");
        await loop2.RunAsync();
        // Combined lines distinct by raw content
        var all = journal1.Lines.Concat(journal2.Lines).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    private sealed class EnumerableTickSource : ITickSource
    {
        private readonly IEnumerable<PriceTick> _ticks;
        public EnumerableTickSource(IEnumerable<PriceTick> ticks) => _ticks = ticks.OrderBy(t => t.UtcTimestamp).ThenBy(t => t.InstrumentId.Value);
        public IEnumerator<PriceTick> GetEnumerator() => _ticks.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TestJournalWriter : IJournalWriter
    {
        public readonly List<string> Lines = new();
        public Task AppendAsync(JournalEvent evt, CancellationToken ct = default)
        {
            Lines.Add(evt.ToCsvLine());
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}