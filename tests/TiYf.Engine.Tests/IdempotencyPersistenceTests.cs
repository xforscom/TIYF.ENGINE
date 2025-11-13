using Microsoft.Extensions.Logging.Abstractions;
using TiYf.Engine.Host;
using TiYf.Engine.Sim;

namespace TiYf.Engine.Tests;

public class IdempotencyPersistenceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"idempotency-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Load_DropsExpiredEntries()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "keys.jsonl");
        var now = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllLines(path, new[]
        {
            "{\"Kind\":0,\"Key\":\"recent\",\"TimestampUtc\":\"2024-01-01T12:00:00Z\"}",
            "{\"Kind\":0,\"Key\":\"expired\",\"TimestampUtc\":\"2023-12-20T12:00:00Z\"}"
        });

        var store = new FileIdempotencyPersistence(
            path,
            TimeSpan.FromHours(24),
            10,
            10,
            NullLogger.Instance,
            () => now);

        var snapshot = store.Load();

        Assert.Single(snapshot.Orders);
        Assert.Equal("recent", snapshot.Orders[0].Key);
        Assert.Equal(1, snapshot.ExpiredDropped);
    }

    [Fact]
    public void AddAndRemove_PersistAcrossLoads()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "persist.jsonl");
        var store = new FileIdempotencyPersistence(
            path,
            TimeSpan.FromHours(24),
            10,
            10,
            NullLogger.Instance,
            () => DateTime.UtcNow);

        store.AddKey(IdempotencyKind.Order, "order-1", DateTime.UtcNow);
        store.AddKey(IdempotencyKind.Cancel, "cancel-1", DateTime.UtcNow);
        store.RemoveKey(IdempotencyKind.Cancel, "cancel-1");

        var snapshot = store.Load();

        Assert.Single(snapshot.Orders);
        Assert.Empty(snapshot.Cancels);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
