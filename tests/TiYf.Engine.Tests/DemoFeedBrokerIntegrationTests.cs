using System;
using System.Globalization;
using System.IO;
using System.Linq;
using TiYf.Engine.DemoFeed;
using TiYf.Engine.Tools;
using Xunit;

namespace TiYf.Engine.Tests;

public sealed class DemoFeedBrokerIntegrationTests
{
    [Fact]
    public void DemoBroker_Smoke_VerifiesStrict()
    {
        var runRoot = CreateTempRoot();
        Directory.CreateDirectory(runRoot);
        try
        {
            var options = DemoFeedOptions.FromArgs(new[]
            {
                "--run-id=" + ("SMOKE-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                "--journal-root=" + runRoot,
                "--start-utc=2025-01-01T00:00:00Z",
                "--bars=120",
                "--interval-seconds=60",
                "--symbols=EURUSD",
                "--broker-enabled=true",
                "--broker-fill-mode=ioc-market"
            });

            DemoFeedResult result = DemoFeedRunner.Run(options);
            Assert.False(result.BrokerHadDanglingPositions);
            Assert.False(string.IsNullOrWhiteSpace(result.TradesPath));
            Assert.True(File.Exists(result.TradesPath!));

            var strict = StrictJournalVerifier.Verify(new StrictVerifyRequest(result.EventsPath, result.TradesPath!, "1.3.0", strict: true));
            Assert.Equal(0, strict.ExitCode);
        }
        finally
        {
            Cleanup(runRoot);
        }
    }

    [Fact]
    public void DemoBroker_Parity_AB_RunsMatch()
    {
        var runRoot = CreateTempRoot();
        Directory.CreateDirectory(runRoot);

        try
        {
            var commonArgs = new[]
            {
                "--journal-root=" + runRoot,
                "--start-utc=2025-01-01T00:00:00Z",
                "--bars=120",
                "--interval-seconds=60",
                "--symbols=EURUSD",
                "--broker-enabled=true",
                "--broker-fill-mode=ioc-market"
            };

            var optionsA = DemoFeedOptions.FromArgs(commonArgs.Concat(new[] { "--run-id=PARITY-A" }).ToArray());
            var resultA = DemoFeedRunner.Run(optionsA);

            var optionsB = DemoFeedOptions.FromArgs(commonArgs.Concat(new[] { "--run-id=PARITY-B" }).ToArray());
            var resultB = DemoFeedRunner.Run(optionsB);

            var parity = ParitySnapshot.Compute(resultA.EventsPath, resultB.EventsPath, resultA.TradesPath, resultB.TradesPath);
            Assert.Equal(0, parity.ExitCode);
            Assert.True(parity.Events.Match);
            Assert.True(parity.Trades?.Match ?? false);
            Assert.False(resultA.BrokerHadDanglingPositions);
            Assert.False(resultB.BrokerHadDanglingPositions);
        }
        finally
        {
            Cleanup(runRoot);
        }
    }

    private static string CreateTempRoot()
    {
        return Path.Combine(Path.GetTempPath(), "demo-feed-integration", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup only
        }
    }
}
