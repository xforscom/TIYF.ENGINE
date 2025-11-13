using Microsoft.Extensions.Logging.Abstractions;
using TiYf.Engine.Host;

namespace TiYf.Engine.Tests;

public class StartupReconciliationRunnerTests
{
    [Fact]
    public async Task RunOnceAsync_InvokesEmitterOnce()
    {
        var calls = 0;
        var runner = new StartupReconciliationRunner(
            () => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            (_, _) =>
            {
                calls++;
                return Task.CompletedTask;
            },
            NullLogger.Instance);

        await runner.RunOnceAsync(CancellationToken.None);
        await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunOnceAsync_SwallowsExceptions()
    {
        var runner = new StartupReconciliationRunner(
            () => DateTime.UtcNow,
            (_, _) => throw new InvalidOperationException("boom"),
            NullLogger.Instance);

        await runner.RunOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RunOnceAsync_HandlesCancellationGracefully()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new StartupReconciliationRunner(
            () => DateTime.UtcNow,
            (_, token) => Task.FromCanceled(token),
            NullLogger.Instance);

        await runner.RunOnceAsync(cts.Token);
    }
}
