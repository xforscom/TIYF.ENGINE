using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiYf.Engine.Core;

namespace TiYf.Engine.Host;

internal static class LoopSnapshotPersistence
{
    private sealed record SnapshotModel(
        string SchemaVersion,
        string EngineInstanceId,
        List<SnapshotBar> Bars,
        long DecisionsTotal = 0,
        long LoopIterationsTotal = 0,
        DateTime? LastDecisionUtc = null,
        Dictionary<string, DateTime?>? DecisionsByTimeframe = null);

    private sealed record SnapshotBar(string InstrumentId, double IntervalSeconds, DateTime OpenTimeUtc);

    internal static LoopSnapshot Load(string path)
    {
        if (!File.Exists(path))
        {
            return LoopSnapshot.Empty;
        }

        var json = File.ReadAllText(path);
        var model = JsonSerializer.Deserialize<SnapshotModel>(json) ?? throw new InvalidOperationException("Invalid loop snapshot");
        var bars = model.Bars
            .OrderBy(b => b.InstrumentId, StringComparer.Ordinal)
            .ThenBy(b => b.IntervalSeconds)
            .ThenBy(b => b.OpenTimeUtc)
            .Select(b => new BarKey(new InstrumentId(b.InstrumentId), new BarInterval(TimeSpan.FromSeconds(b.IntervalSeconds)), DateTime.SpecifyKind(b.OpenTimeUtc, DateTimeKind.Utc)))
            .ToList();
        var tracker = new InMemoryBarKeyTracker(bars);
        var decisions = model.DecisionsByTimeframe is null
            ? new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, DateTime?>(model.DecisionsByTimeframe, StringComparer.OrdinalIgnoreCase);
        DateTime? lastDecision = model.LastDecisionUtc.HasValue
            ? DateTime.SpecifyKind(model.LastDecisionUtc.Value, DateTimeKind.Utc)
            : null;

        return new LoopSnapshot(
            model.EngineInstanceId,
            tracker,
            model.DecisionsTotal,
            model.LoopIterationsTotal,
            lastDecision,
            decisions);
    }

    internal static void Save(string path, LoopSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var bars = snapshot.Tracker.Snapshot()
            .OrderBy(b => b.InstrumentId.Value, StringComparer.Ordinal)
            .ThenBy(b => b.Interval.Duration.TotalSeconds)
            .ThenBy(b => b.OpenTimeUtc)
            .Select(b => new SnapshotBar(b.InstrumentId.Value, b.Interval.Duration.TotalSeconds, b.OpenTimeUtc))
            .ToList();

        var decisions = snapshot.DecisionsByTimeframe
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        var model = new SnapshotModel(
            TiYf.Engine.Core.Infrastructure.Schema.Version,
            snapshot.EngineInstanceId,
            bars,
            snapshot.DecisionsTotal,
            snapshot.LoopIterationsTotal,
            snapshot.LastDecisionUtc,
            decisions);

        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        File.Move(tmp, path);
    }
}

internal sealed record LoopSnapshot(
    string EngineInstanceId,
    InMemoryBarKeyTracker Tracker,
    long DecisionsTotal,
    long LoopIterationsTotal,
    DateTime? LastDecisionUtc,
    IReadOnlyDictionary<string, DateTime?> DecisionsByTimeframe)
{
    internal static LoopSnapshot Empty { get; } = new(
        string.Empty,
        new InMemoryBarKeyTracker(),
        0,
        0,
        null,
        new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase));
}
