using TiYf.Engine.Core;
using TiYf.Engine.Sim;

namespace TiYf.Engine.Tests;

public class UtcAndPersistenceTests
{
    [Fact]
    public void UtcGuard_ThrowsOnNonUtcTicks()
    {
        var nonUtc = new DateTime(2025, 10, 5, 12, 0, 0, DateTimeKind.Local);
        Assert.Throws<ArgumentException>(() => new PriceTick(new InstrumentId("X"), nonUtc, 1m, 1m));
    }

    [Fact]
    public void BarKeySnapshot_PersistsAndPreventsDuplicatesOnRestart()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "barKeyTest");
        Directory.CreateDirectory(tmpDir);
        var snapshot = Path.Combine(tmpDir, "snapshot.json");
        if (File.Exists(snapshot)) File.Delete(snapshot);
        var tracker = new InMemoryBarKeyTracker();
        var key = new BarKey(new InstrumentId("EURUSD"), BarInterval.OneMinute, new DateTime(2025, 10, 5, 10, 0, 0, DateTimeKind.Utc));
        tracker.Add(key);
        BarKeyTrackerPersistence.Save(snapshot, tracker, "1.1.0", "engine-test");
        var reloaded = BarKeyTrackerPersistence.Load(snapshot);
        Assert.True(reloaded.Seen(key));
        // Idempotency: saving again without duplicate entries
        BarKeyTrackerPersistence.Save(snapshot, reloaded, "1.1.0", "engine-test");
        var again = BarKeyTrackerPersistence.Load(snapshot);
        Assert.True(again.Seen(key));
    }
}