using System;
using System.Collections.Generic;

namespace TiYf.Engine.DemoFeed;

internal sealed record DemoBrokerOptions(bool Enabled, string FillMode, int? Seed);

internal sealed record DemoBarSnapshot(DateTime Timestamp, decimal Close);

internal sealed record DemoTradeRecord(
    DateTime UtcTsOpen,
    DateTime UtcTsClose,
    string Symbol,
    string Direction,
    decimal EntryPrice,
    decimal ExitPrice,
    long VolumeUnits,
    decimal PnlCcy,
    decimal PnlR,
    string DecisionId);

internal sealed record DemoBrokerResult(IReadOnlyList<DemoTradeRecord> Trades, bool HadDanglingPositions);
