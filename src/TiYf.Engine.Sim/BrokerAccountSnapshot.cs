using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TiYf.Engine.Sim;

public sealed record BrokerPositionSnapshot(
    string Symbol,
    TradeSide Side,
    long Units,
    decimal? AveragePrice);

public sealed record BrokerOrderSnapshot(
    string BrokerOrderId,
    string Symbol,
    TradeSide Side,
    long Units,
    decimal? Price,
    string Status);

public sealed record BrokerAccountSnapshot(
    DateTime UtcTimestamp,
    IReadOnlyList<BrokerPositionSnapshot> Positions,
    IReadOnlyList<BrokerOrderSnapshot> Orders);

public interface IBrokerAccountSnapshotProvider
{
    Task<BrokerAccountSnapshot> GetBrokerAccountSnapshotAsync(CancellationToken ct = default);
}
