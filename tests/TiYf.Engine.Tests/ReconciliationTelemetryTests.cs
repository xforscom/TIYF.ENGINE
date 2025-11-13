using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TiYf.Engine.Host;
using TiYf.Engine.Sim;

namespace TiYf.Engine.Tests;

public class ReconciliationTelemetryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"reconcile-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Telemetry_WritesMatchRecord()
    {
        Directory.CreateDirectory(_tempDir);
        var state = new EngineHostState("oanda-demo", Array.Empty<string>());
        var now = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var enginePositions = new[]
        {
            ("EURUSD", TradeSide.Buy, 1.2500m, 1_000L, now)
        };
        var brokerSnapshot = new BrokerAccountSnapshot(
            now,
            new[]
            {
                new BrokerPositionSnapshot("EURUSD", TradeSide.Buy, 1_000L, 1.25m)
            },
            Array.Empty<BrokerOrderSnapshot>());
        await using var writer = new ReconciliationJournalWriter(_tempDir, "oanda-demo", "run-1", "hash", "acct-1");
        var telemetry = new ReconciliationTelemetry(
            () => enginePositions,
            _ => Task.FromResult<BrokerAccountSnapshot?>(brokerSnapshot),
            writer,
            state,
            NullLogger.Instance);

        await telemetry.EmitAsync(now, CancellationToken.None);

        var files = Directory.GetFiles(Path.Combine(_tempDir, "oanda-demo", "run-1"), "*.jsonl", SearchOption.AllDirectories);
        Assert.Single(files);
        var lines = await File.ReadAllLinesAsync(files[0]);
        Assert.Contains(lines, line => line.Contains("\"status\":\"match\"", StringComparison.Ordinal));
        var health = state.CreateHealthPayload();
        var json = JsonSerializer.Serialize(health);
        using var document = JsonDocument.Parse(json);
        var reconciliation = document.RootElement.GetProperty("reconciliation");
        Assert.Equal("match", reconciliation.GetProperty("last_status").GetString());
    }

    [Fact]
    public async Task Telemetry_IncrementsMismatchCounter()
    {
        Directory.CreateDirectory(_tempDir);
        var state = new EngineHostState("oanda-demo", Array.Empty<string>());
        var now = DateTime.UtcNow;
        var enginePositions = new[]
        {
            ("GBPUSD", TradeSide.Sell, 1.2100m, 2_000L, now)
        };
        var brokerSnapshot = new BrokerAccountSnapshot(
            now,
            new[]
            {
                new BrokerPositionSnapshot("GBPUSD", TradeSide.Sell, 1_000L, 1.21m)
            },
            Array.Empty<BrokerOrderSnapshot>());
        await using var writer = new ReconciliationJournalWriter(_tempDir, "oanda-demo", "run-2", "hash", "acct-1");
        var telemetry = new ReconciliationTelemetry(
            () => enginePositions,
            _ => Task.FromResult<BrokerAccountSnapshot?>(brokerSnapshot),
            writer,
            state,
            NullLogger.Instance);

        await telemetry.EmitAsync(now, CancellationToken.None);

        var snapshot = state.CreateMetricsSnapshot();
        Assert.Equal(1, snapshot.ReconciliationMismatchesTotal);
        Assert.Equal("mismatch", snapshot.ReconciliationLastStatus);
        Assert.True(snapshot.ReconciliationLastUnixSeconds.HasValue);
    }

    [Fact]
    public void RecordBuilder_AggregatesSignedNotionalCorrectly()
    {
        var now = new DateTime(2024, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        var enginePositions = new[]
        {
            ("EURUSD", TradeSide.Buy, 1.2500m, 1_500L, now),
            ("EURUSD", TradeSide.Sell, 1.2600m, 500L, now)
        };
        var brokerSnapshot = new BrokerAccountSnapshot(
            now,
            new[]
            {
                new BrokerPositionSnapshot("EURUSD", TradeSide.Buy, 1_000L, 1.2450m)
            },
            Array.Empty<BrokerOrderSnapshot>());

        var records = ReconciliationRecordBuilder.Build(now, enginePositions, brokerSnapshot);
        var record = Assert.Single(records);
        Assert.Equal(ReconciliationStatus.Match, record.Status);
        Assert.Equal("aligned", record.Reason);
        Assert.NotNull(record.EnginePosition);
        Assert.Equal(1.2450m, record.EnginePosition!.AveragePrice);
    }

    [Fact]
    public void RecordBuilder_PureShort_ProducesPositiveAveragePrice()
    {
        var now = new DateTime(2024, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var enginePositions = new[]
        {
            ("USDJPY", TradeSide.Sell, 148.2500m, 100_000L, now)
        };
        var brokerSnapshot = new BrokerAccountSnapshot(
            now,
            new[]
            {
                new BrokerPositionSnapshot("USDJPY", TradeSide.Sell, 100_000L, 148.2500m)
            },
            Array.Empty<BrokerOrderSnapshot>());

        var records = ReconciliationRecordBuilder.Build(now, enginePositions, brokerSnapshot);
        var record = Assert.Single(records);
        Assert.Equal(ReconciliationStatus.Match, record.Status);
        Assert.Equal("aligned", record.Reason);
        Assert.NotNull(record.EnginePosition);
        Assert.Equal(148.2500m, record.EnginePosition!.AveragePrice);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
