using ReconciliationProbe;

namespace TiYf.Engine.Tests;

public sealed class ReconciliationProbeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"reconcile-probe-{Guid.NewGuid():N}");

    [Fact]
    public void Analyze_MatchOnlyFixture_ReturnsZeroMismatches()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "TestArtifacts", "ReconcileFixtures", "match");
        var result = ReconciliationProbeRunner.Analyze(root);

        Assert.Equal(2, result.TotalRecords);
        Assert.Equal(0, result.MismatchesTotal);
        Assert.Empty(result.MismatchesByReason);
        Assert.Empty(result.SymbolsWithMismatch);

        ReconciliationProbeRunner.WriteArtifacts(result, _tempDir);
        var summary = File.ReadAllText(Path.Combine(_tempDir, "summary.txt")).Trim();
        Assert.Contains("mismatches_total=0", summary, StringComparison.OrdinalIgnoreCase);
        var metrics = File.ReadAllText(Path.Combine(_tempDir, "metrics.txt"));
        Assert.Contains("reconciler_records_total 2", metrics);
        Assert.Contains("reconciler_mismatches_total 0", metrics);
    }

    [Fact]
    public void Analyze_MixedFixture_AggregatesReasons()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "TestArtifacts", "ReconcileFixtures", "mismatch");
        var result = ReconciliationProbeRunner.Analyze(root, adapterFilter: "oanda-demo");

        Assert.Equal(3, result.TotalRecords);
        Assert.Equal(2, result.MismatchesTotal);
        Assert.Equal(2, result.MismatchesByReason.Count);
        Assert.Equal(1, result.MismatchesByReason["price_diff"]);
        Assert.Equal(1, result.MismatchesByReason["units_diff"]);
        Assert.Contains("EURUSD", result.SymbolsWithMismatch);
        Assert.Contains("USDJPY", result.SymbolsWithMismatch);
        Assert.True(result.LastReconcileUtc.HasValue);

        ReconciliationProbeRunner.WriteArtifacts(result, _tempDir);
        var summary = File.ReadAllText(Path.Combine(_tempDir, "summary.txt")).Trim();
        Assert.Contains("mismatches_total=2", summary);
        var metrics = File.ReadAllText(Path.Combine(_tempDir, "metrics.txt"));
        Assert.Contains("reconciler_mismatches_by_reason{reason=\"price_diff\"} 1", metrics);
        Assert.Contains("reconciler_mismatches_by_reason{reason=\"units_diff\"} 1", metrics);
        var health = File.ReadAllText(Path.Combine(_tempDir, "health.json"));
        Assert.Contains("\"mismatches_total\": 2", health);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
