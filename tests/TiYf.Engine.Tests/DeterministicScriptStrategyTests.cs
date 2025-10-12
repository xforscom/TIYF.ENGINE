using TiYf.Engine.Sim;
using TiYf.Engine.Core;

namespace TiYf.Engine.Tests;

public class DeterministicScriptStrategyTests
{
    [Fact]
    public void DeterministicScriptStrategy_EmitsStableProposals_GivenM0Clock()
    {
        // Arrange: synthetic start at an aligned minute
        var start = new DateTime(2025, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        var seq = Enumerable.Range(0, 120).Select(i => start.AddMinutes(i)).ToList(); // 120 minutes window
        var clock = new DeterministicSequenceClock(seq);
        var instruments = new[] { new Instrument(new InstrumentId("I_EURUSD"), "EURUSD", 5), new Instrument(new InstrumentId("I_USDJPY"), "USDJPY", 3), new Instrument(new InstrumentId("I_XAUUSD"), "XAUUSD", 2) };
        var strat = new DeterministicScriptStrategy(clock, instruments, seq.First());

        var emitted = new List<DeterministicScriptStrategy.ScheduledAction>();

        // Act: walk the clock sequence minute by minute
        foreach (var ts in seq)
        {
            clock.Tick();
            var aligned = new DateTime(clock.UtcNow.Year, clock.UtcNow.Month, clock.UtcNow.Day, clock.UtcNow.Hour, clock.UtcNow.Minute, 0, DateTimeKind.Utc);
            emitted.AddRange(strat.Pending(aligned));
        }

        // Assert: for each symbol we expect 4 scheduled actions (BUY open/close, SELL open/close)
        foreach (var symbol in new[] { "EURUSD", "USDJPY", "XAUUSD" })
        {
            var bySym = emitted.Where(e => e.Symbol == symbol).OrderBy(e => e.WhenUtc).ToList();
            Assert.Equal(4, bySym.Count);
            // BUY open at +15m
            var buyOpen = bySym[0];
            Assert.Equal(symbol, buyOpen.Symbol);
            Assert.Equal(Side.Buy, buyOpen.Side);
            Assert.Equal(new DateTime(2025, 10, 5, 0, 15, 0, DateTimeKind.Utc), buyOpen.WhenUtc);
            Assert.Equal($"M0-{symbol}-01", buyOpen.DecisionId);
            // BUY close at +45m
            var buyClose = bySym[1];
            Assert.Equal(Side.Close, buyClose.Side);
            Assert.Equal(new DateTime(2025, 10, 5, 0, 45, 0, DateTimeKind.Utc), buyClose.WhenUtc);
            // SELL open at +75m
            var sellOpen = bySym[2];
            Assert.Equal(Side.Sell, sellOpen.Side);
            Assert.Equal(new DateTime(2025, 10, 5, 1, 15, 0, DateTimeKind.Utc), sellOpen.WhenUtc);
            Assert.Equal($"M0-{symbol}-02", sellOpen.DecisionId);
            // SELL close at +105m (1:45)
            var sellClose = bySym[3];
            Assert.Equal(Side.Close, sellClose.Side);
            Assert.Equal(new DateTime(2025, 10, 5, 1, 45, 0, DateTimeKind.Utc), sellClose.WhenUtc);
            // DecisionIds stable
            Assert.Equal($"M0-{symbol}-01", buyClose.DecisionId);
            Assert.Equal($"M0-{symbol}-02", sellClose.DecisionId);
        }

        // Ensure no duplicates
        var dup = emitted.GroupBy(e => (e.Symbol, e.WhenUtc, e.Side)).Where(g => g.Count() > 1).ToList();
        Assert.Empty(dup);
    }
}
