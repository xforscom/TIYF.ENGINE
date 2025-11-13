using System.Linq;
using System.Text.Json.Serialization;
using TiYf.Engine.Sim;

namespace TiYf.Engine.Host;

public enum ReconciliationStatus
{
    Match = 0,
    Unknown = 1,
    Mismatch = 2
}

internal sealed record ReconciliationPositionView(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("units")] long Units,
    [property: JsonPropertyName("avg_price")] decimal? AveragePrice);

internal sealed record ReconciliationOrderView(
    [property: JsonPropertyName("broker_order_id")] string BrokerOrderId,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("units")] long Units,
    [property: JsonPropertyName("price")] decimal? Price,
    [property: JsonPropertyName("status")] string Status);

internal sealed record ReconciliationRecord(
    DateTime UtcTimestamp,
    string Symbol,
    ReconciliationPositionView? EnginePosition,
    ReconciliationPositionView? BrokerPosition,
    IReadOnlyList<ReconciliationOrderView> EngineOrders,
    IReadOnlyList<ReconciliationOrderView> BrokerOrders,
    ReconciliationStatus Status,
    string Reason);

internal static class ReconciliationRecordBuilder
{
    private sealed record AggregatedPosition(long SignedUnits, decimal Notional);

    public static IReadOnlyList<ReconciliationRecord> Build(
        DateTime utcNow,
        IReadOnlyCollection<(string Symbol, TradeSide Side, decimal EntryPrice, long Units, DateTime OpenTimestamp)> enginePositions,
        BrokerAccountSnapshot? broker)
    {
        var engineMap = AggregateEnginePositions(enginePositions);
        var brokerMap = AggregateBrokerPositions(broker);
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in engineMap.Keys)
        {
            symbols.Add(key);
        }
        foreach (var key in brokerMap.Keys)
        {
            symbols.Add(key);
        }
        if (symbols.Count == 0)
        {
            symbols.Add("*");
        }

        var records = new List<ReconciliationRecord>(symbols.Count);
        var brokerAvailable = broker is not null;
        foreach (var symbol in symbols.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            engineMap.TryGetValue(symbol, out var enginePosition);
            brokerMap.TryGetValue(symbol, out var brokerPosition);
            var (status, reason) = DetermineStatus(enginePosition, brokerPosition, brokerAvailable);
            var brokerOrders = FilterOrders(broker?.Orders, symbol);
            records.Add(new ReconciliationRecord(
                utcNow,
                symbol,
                enginePosition,
                brokerPosition,
                Array.Empty<ReconciliationOrderView>(),
                brokerOrders,
                status,
                reason));
        }
        return records;
    }

    private static IReadOnlyDictionary<string, ReconciliationPositionView> AggregateEnginePositions(
        IReadOnlyCollection<(string Symbol, TradeSide Side, decimal EntryPrice, long Units, DateTime OpenTimestamp)> snapshot)
    {
        if (snapshot.Count == 0)
        {
            return new Dictionary<string, ReconciliationPositionView>(StringComparer.OrdinalIgnoreCase);
        }

        var aggregates = new Dictionary<string, AggregatedPosition>(StringComparer.OrdinalIgnoreCase);
        foreach (var position in snapshot)
        {
            if (string.IsNullOrWhiteSpace(position.Symbol) || position.Units <= 0)
            {
                continue;
            }

            var key = position.Symbol.ToUpperInvariant();
            if (!aggregates.TryGetValue(key, out var agg))
            {
                agg = new AggregatedPosition(0, 0m);
            }

            var signedUnits = position.Side == TradeSide.Buy ? position.Units : -position.Units;
            var notional = agg.Notional + (position.EntryPrice * position.Units);
            var updated = agg with { SignedUnits = agg.SignedUnits + signedUnits, Notional = notional };
            aggregates[key] = updated;
        }

        var result = new Dictionary<string, ReconciliationPositionView>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in aggregates)
        {
            var signed = kvp.Value.SignedUnits;
            if (signed == 0)
            {
                continue;
            }

            var side = signed > 0 ? "buy" : "sell";
            var units = Math.Abs(signed);
            decimal? avgPrice = null;
            if (units > 0)
            {
                avgPrice = decimal.Round(kvp.Value.Notional / units, 6, MidpointRounding.AwayFromZero);
            }
            result[kvp.Key] = new ReconciliationPositionView(kvp.Key, side, units, avgPrice);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, ReconciliationPositionView> AggregateBrokerPositions(BrokerAccountSnapshot? broker)
    {
        if (broker is null || broker.Positions.Count == 0)
        {
            return new Dictionary<string, ReconciliationPositionView>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, ReconciliationPositionView>(StringComparer.OrdinalIgnoreCase);
        foreach (var position in broker.Positions)
        {
            if (string.IsNullOrWhiteSpace(position.Symbol) || position.Units <= 0)
            {
                continue;
            }

            var symbol = position.Symbol.ToUpperInvariant();
            var side = position.Side == TradeSide.Buy ? "buy" : "sell";
            decimal? avg = null;
            if (position.AveragePrice.HasValue)
            {
                avg = decimal.Round(position.AveragePrice.Value, 6, MidpointRounding.AwayFromZero);
            }
            result[symbol] = new ReconciliationPositionView(symbol, side, position.Units, avg);
        }

        return result;
    }

    private static (ReconciliationStatus Status, string Reason) DetermineStatus(
        ReconciliationPositionView? engine,
        ReconciliationPositionView? broker,
        bool brokerAvailable)
    {
        if (!brokerAvailable)
        {
            return (ReconciliationStatus.Unknown, "broker_snapshot_unavailable");
        }

        if (engine is null && broker is null)
        {
            return (ReconciliationStatus.Match, "flat");
        }

        if (engine is null)
        {
            return (ReconciliationStatus.Mismatch, "engine_missing");
        }

        if (broker is null)
        {
            return (ReconciliationStatus.Mismatch, "broker_missing");
        }

        if (!string.Equals(engine.Side, broker.Side, StringComparison.OrdinalIgnoreCase))
        {
            return (ReconciliationStatus.Mismatch, "side_diff");
        }

        if (engine.Units != broker.Units)
        {
            return (ReconciliationStatus.Mismatch, "units_diff");
        }

        if (engine.AveragePrice.HasValue && broker.AveragePrice.HasValue)
        {
            var diff = Math.Abs(engine.AveragePrice.Value - broker.AveragePrice.Value);
            if (diff > 0.00001m)
            {
                return (ReconciliationStatus.Mismatch, "price_diff");
            }
        }

        return (ReconciliationStatus.Match, "aligned");
    }

    private static IReadOnlyList<ReconciliationOrderView> FilterOrders(IReadOnlyList<BrokerOrderSnapshot>? orders, string symbol)
    {
        if (orders is null || orders.Count == 0)
        {
            return Array.Empty<ReconciliationOrderView>();
        }

        var filtered = new List<ReconciliationOrderView>();
        foreach (var order in orders)
        {
            if (string.IsNullOrWhiteSpace(order.Symbol))
            {
                continue;
            }

            if (!string.Equals(order.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var side = order.Side == TradeSide.Buy ? "buy" : "sell";
            filtered.Add(new ReconciliationOrderView(
                order.BrokerOrderId,
                order.Symbol.ToUpperInvariant(),
                side,
                order.Units,
                order.Price,
                order.Status));
        }

        return filtered;
    }
}
