using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Host;
using Xunit;

namespace TiYf.Engine.Tests;

public sealed class LoopSnapshotPersistenceTests : IDisposable
{
    private readonly string _workspace;

    public LoopSnapshotPersistenceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"loop-snapshot-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    [Fact]
    public void SaveLoad_Roundtrip_IsDeterministic()
    {
        var path = Path.Combine(_workspace, "snapshot.json");
        var schema = TiYf.Engine.Core.Infrastructure.Schema.Version;
        var loopId = "loop-test-1234";
        var tracker = new InMemoryBarKeyTracker(new[]
        {
            new BarKey(new InstrumentId("EURUSD"), new BarInterval(TimeSpan.FromHours(1)), new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            new BarKey(new InstrumentId("EURUSD"), new BarInterval(TimeSpan.FromHours(4)), new DateTime(2024, 1, 1, 4, 0, 0, DateTimeKind.Utc))
        });
        var decisions = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase)
        {
            ["H1"] = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc),
            ["H4"] = new DateTime(2024, 1, 1, 4, 0, 0, DateTimeKind.Utc)
        };
        var original = new LoopSnapshot(
            schema,
            loopId,
            "live",
            tracker,
            DecisionsTotal: 3,
            LoopIterationsTotal: 3,
            LastDecisionUtc: new DateTime(2024, 1, 1, 4, 0, 0, DateTimeKind.Utc),
            DecisionsByTimeframe: decisions);

        LoopSnapshotPersistence.Save(path, original);
        var firstWrite = File.ReadAllText(path);

        var loaded = LoopSnapshotPersistence.Load(path);
        Assert.Equal(schema, loaded.SchemaVersion);
        Assert.Equal(loopId, loaded.EngineInstanceId);
        Assert.Equal("live", loaded.Source);
        Assert.Equal(3, loaded.DecisionsTotal);
        Assert.Equal(3, loaded.LoopIterationsTotal);
        Assert.Equal(original.LastDecisionUtc, loaded.LastDecisionUtc);
        Assert.True(loaded.DecisionsByTimeframe.ContainsKey("H1"));

        // Save the loaded snapshot back to disk and ensure byte-for-byte determinism.
        LoopSnapshotPersistence.Save(path, loaded);
        var secondWrite = File.ReadAllText(path);
        Assert.Equal(firstWrite, secondWrite);
    }

    [Fact]
    public void Load_IgnoresPartialTempWrites()
    {
        var path = Path.Combine(_workspace, "snapshot.json");
        var tracker = new InMemoryBarKeyTracker();
        tracker.Add(new BarKey(new InstrumentId("GBPUSD"), new BarInterval(TimeSpan.FromHours(1)), new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc)));

        var snapshot = new LoopSnapshot(
            TiYf.Engine.Core.Infrastructure.Schema.Version,
            "loop-partial",
            "live",
            tracker,
            DecisionsTotal: 1,
            LoopIterationsTotal: 1,
            LastDecisionUtc: new DateTime(2024, 3, 1, 1, 0, 0, DateTimeKind.Utc),
            DecisionsByTimeframe: new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase)
            {
                ["H1"] = new DateTime(2024, 3, 1, 1, 0, 0, DateTimeKind.Utc)
            });

        LoopSnapshotPersistence.Save(path, snapshot);

        // Simulate a leftover temp file from an interrupted write.
        File.WriteAllText(path + ".tmp", "{ \"corrupt\": true }");

        var loaded = LoopSnapshotPersistence.Load(path);
        Assert.Equal("loop-partial", loaded.EngineInstanceId);
        Assert.Equal(1, loaded.DecisionsTotal);
        Assert.True(loaded.Tracker.Seen(new BarKey(new InstrumentId("GBPUSD"), new BarInterval(TimeSpan.FromHours(1)), new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc))));
    }

    [Fact]
    public void Resume_DuplicateGuard_PreventsReprocessing()
    {
        var path = Path.Combine(_workspace, "resume.json");
        var bar1 = new BarKey(new InstrumentId("USDJPY"), new BarInterval(TimeSpan.FromHours(1)), new DateTime(2024, 5, 10, 0, 0, 0, DateTimeKind.Utc));
        var tracker = new InMemoryBarKeyTracker(new[] { bar1 });
        var decisions = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase)
        {
            ["H1"] = new DateTime(2024, 5, 10, 1, 0, 0, DateTimeKind.Utc)
        };

        var snapshot = new LoopSnapshot(
            TiYf.Engine.Core.Infrastructure.Schema.Version,
            "loop-resume",
            "live",
            tracker,
            DecisionsTotal: 1,
            LoopIterationsTotal: 1,
            LastDecisionUtc: decisions["H1"],
            DecisionsByTimeframe: decisions);

        LoopSnapshotPersistence.Save(path, snapshot);

        var loaded = LoopSnapshotPersistence.Load(path);
        Assert.True(loaded.Tracker.Seen(bar1));

        var bar2 = new BarKey(new InstrumentId("USDJPY"), new BarInterval(TimeSpan.FromHours(1)), new DateTime(2024, 5, 10, 1, 0, 0, DateTimeKind.Utc));
        Assert.False(loaded.Tracker.Seen(bar2));
        loaded.Tracker.Add(bar2);
        Assert.True(loaded.Tracker.Seen(bar2));

        var state = new EngineHostState("oanda-demo", Array.Empty<string>());
        state.SetTimeframes(new[] { "H1" });
        state.BootstrapLoopState(loaded.DecisionsTotal, loaded.LoopIterationsTotal, loaded.LastDecisionUtc, loaded.DecisionsByTimeframe);
        var nextDecision = new DateTime(2024, 5, 10, 2, 0, 0, DateTimeKind.Utc);
        state.RecordLoopDecision("H1", nextDecision);

        var payload = state.CreateHealthPayload();
        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(nextDecision.ToString("O"), root.GetProperty("last_decision_utc").GetString());
        Assert.Equal(2, root.GetProperty("decisions_total").GetInt64());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
