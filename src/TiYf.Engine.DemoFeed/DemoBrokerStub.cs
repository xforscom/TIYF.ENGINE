using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TiYf.Engine.DemoFeed;

internal static class DemoBrokerStub
{
    private static readonly TimeSpan[] TradeOffsets =
    {
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(75)
    };

    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(30);

    private const long VolumeUnits = 100;

    public static DemoBrokerResult GenerateTrades(
        DemoFeedOptions options,
        IReadOnlyDictionary<string, List<DemoBarSnapshot>> barsBySymbol)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (barsBySymbol is null) throw new ArgumentNullException(nameof(barsBySymbol));

        if (!options.Broker.Enabled)
        {
            return new DemoBrokerResult(Array.Empty<DemoTradeRecord>(), false);
        }

        if (!string.Equals(options.Broker.FillMode, "ioc-market", StringComparison.OrdinalIgnoreCase))
        {
            throw new DemoFeedException($"Unsupported demo broker fill mode '{options.Broker.FillMode}'.");
        }

        var trades = new List<DemoTradeRecord>();
        bool hadDangling = false;

        foreach (var kvp in barsBySymbol.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var symbolTrades = BuildTradesForSymbol(options, kvp.Key, kvp.Value, out var symbolDangling);
            trades.AddRange(symbolTrades);
            if (symbolDangling) hadDangling = true;
        }

        trades.Sort(static (a, b) =>
        {
            var compareTs = a.UtcTsOpen.CompareTo(b.UtcTsOpen);
            if (compareTs != 0) return compareTs;
            return string.Compare(a.Symbol, b.Symbol, StringComparison.Ordinal);
        });

        return new DemoBrokerResult(trades, hadDangling);
    }

    private static IReadOnlyList<DemoTradeRecord> BuildTradesForSymbol(
        DemoFeedOptions options,
        string symbol,
        List<DemoBarSnapshot> bars,
        out bool hadDangling)
    {
        hadDangling = false;
        if (bars.Count == 0)
        {
            return Array.Empty<DemoTradeRecord>();
        }

        var byTimestamp = bars.ToDictionary(b => b.Timestamp, b => b);
        var result = new List<DemoTradeRecord>();
        int ordinal = 1;

        foreach (var offset in TradeOffsets)
        {
            var openTs = options.StartUtc.Add(offset);
            var closeTs = openTs.Add(HoldDuration);

            if (!byTimestamp.TryGetValue(openTs, out var openBar))
            {
                continue;
            }

            if (!byTimestamp.TryGetValue(closeTs, out var closeBar))
            {
                hadDangling = true;
                continue;
            }

            var decisionId = ComputeDecisionId(symbol, ordinal, openTs);
            var entryPrice = openBar.Close;
            var exitPrice = closeBar.Close;
            var pnl = decimal.Round((exitPrice - entryPrice) * VolumeUnits, 6, MidpointRounding.AwayFromZero);

            var brokerOrderId = $"STUB-{decisionId}";
            result.Add(new DemoTradeRecord(
                UtcTsOpen: openTs,
                UtcTsClose: closeTs,
                Symbol: symbol,
                Direction: "BUY",
                EntryPrice: entryPrice,
                ExitPrice: exitPrice,
                VolumeUnits: VolumeUnits,
                PnlCcy: pnl,
                PnlR: 0m,
                DecisionId: decisionId,
                BrokerOrderId: brokerOrderId));

            ordinal++;
        }

        return result;
    }

    private static string ComputeDecisionId(string symbol, int ordinal, DateTime openTs)
    {
        var payload = $"{symbol}|{ordinal}|{openTs:O}|TIYF-DEMO-BROKER";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var digest = SHA256.HashData(bytes);
        var hex = Convert.ToHexString(digest.AsSpan(0, 8));
        return $"DEMO-{hex}";
    }
}
