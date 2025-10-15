using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TiYf.Engine.DemoFeed;
using Xunit;

namespace TiYf.Engine.Tests;

public sealed class DemoBrokerStubTests
{
    [Fact]
    public void GenerateTrades_IsDeterministic_ForIdenticalBars()
    {
        var options = CreateOptions();
        var bars = BuildBars(options, new (int offsetMinutes, decimal close)[]
        {
            (15, 1.1000m),
            (45, 1.1020m),
            (75, 1.1010m),
            (105, 1.1005m)
        });
        var book = new Dictionary<string, List<DemoBarSnapshot>>(StringComparer.Ordinal)
        {
            ["EURUSD"] = bars
        };

        var first = DemoBrokerStub.GenerateTrades(options, book);
        var second = DemoBrokerStub.GenerateTrades(options, book);

        Assert.True(first.Trades.SequenceEqual(second.Trades));
        Assert.Equal(ComputeHash(first.Trades), ComputeHash(second.Trades));
    }

    [Fact]
    public void GenerateTrades_ComputesExpectedPnl()
    {
        var options = CreateOptions();
        var bars = BuildBars(options, new (int offsetMinutes, decimal close)[]
        {
            (15, 1.1000m),
            (45, 1.1020m)
        });
        var book = new Dictionary<string, List<DemoBarSnapshot>>(StringComparer.Ordinal)
        {
            ["EURUSD"] = bars
        };

        var result = DemoBrokerStub.GenerateTrades(options, book);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(0.20m, trade.PnlCcy);
        Assert.Equal(0m, trade.PnlR);
    }

    [Fact]
    public void FromArgs_ParsesBrokerFlags()
    {
        var options = DemoFeedOptions.FromArgs(new[]
        {
            "--run-id=UNIT-CLI",
            "--journal-root=" + CreateTempRoot(),
            "--broker-enabled=true",
            "--broker-fill-mode=ioc-market",
            "--broker-seed=9001"
        });

        Assert.True(options.Broker.Enabled);
        Assert.Equal("ioc-market", options.Broker.FillMode);
        Assert.Equal(9001, options.Broker.Seed);
    }

    [Fact]
    public void GenerateTrades_DoesNotReportDangling_WhenClosuresPresent()
    {
        var options = CreateOptions();
        var bars = BuildBars(options, new (int offsetMinutes, decimal close)[]
        {
            (15, 1.1000m),
            (45, 1.1020m),
            (75, 1.1010m),
            (105, 1.1015m)
        });
        var book = new Dictionary<string, List<DemoBarSnapshot>>(StringComparer.Ordinal)
        {
            ["EURUSD"] = bars
        };

        var result = DemoBrokerStub.GenerateTrades(options, book);
        Assert.False(result.HadDanglingPositions);
    }

    private static DemoFeedOptions CreateOptions()
    {
        var args = new List<string>
        {
            "--run-id=UNIT-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            "--journal-root=" + CreateTempRoot(),
            "--start-utc=2025-01-01T00:00:00Z",
            "--bars=120",
            "--interval-seconds=60",
            "--symbols=EURUSD",
            "--broker-enabled=true",
            "--broker-fill-mode=ioc-market"
        };

        return DemoFeedOptions.FromArgs(args.ToArray());
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "demo-broker-tests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        return root;
    }

    private static List<DemoBarSnapshot> BuildBars(DemoFeedOptions options, IEnumerable<(int offsetMinutes, decimal close)> entries)
    {
        return entries
            .Select(entry => new DemoBarSnapshot(options.StartUtc.AddMinutes(entry.offsetMinutes), entry.close))
            .OrderBy(snapshot => snapshot.Timestamp)
            .ToList();
    }

    private static string ComputeHash(IReadOnlyList<DemoTradeRecord> trades)
    {
        var buffer = string.Join('\n', trades.Select(t => string.Join('|', new[]
        {
            t.UtcTsOpen.ToString("O", CultureInfo.InvariantCulture),
            t.UtcTsClose.ToString("O", CultureInfo.InvariantCulture),
            t.Symbol,
            t.Direction,
            t.EntryPrice.ToString("F4", CultureInfo.InvariantCulture),
            t.ExitPrice.ToString("F4", CultureInfo.InvariantCulture),
            t.VolumeUnits.ToString(CultureInfo.InvariantCulture),
            t.PnlCcy.ToString("F2", CultureInfo.InvariantCulture),
            t.PnlR.ToString("F2", CultureInfo.InvariantCulture),
            t.DecisionId
        })));
        var bytes = Encoding.UTF8.GetBytes(buffer);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
