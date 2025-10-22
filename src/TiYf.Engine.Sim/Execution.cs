using System;
using System.Globalization;
using TiYf.Engine.Core;

namespace TiYf.Engine.Sim;

public enum TradeSide { Buy, Sell }

public sealed record OrderRequest(
    string DecisionId,
    string Symbol,
    TradeSide Side,
    long Units,
    DateTime UtcTs
);

public sealed record ExecutionFill(
    string DecisionId,
    string Symbol,
    TradeSide Side,
    decimal Price,
    long Units,
    DateTime UtcTs
);

public sealed record ExecutionResult(bool Accepted, string Reason, ExecutionFill? Fill, string? BrokerOrderId);

public interface IExecutionAdapter
{
    Task<ExecutionResult> ExecuteMarketAsync(OrderRequest order, CancellationToken ct = default);
}

public interface IConnectableExecutionAdapter : IExecutionAdapter
{
    Task ConnectAsync(CancellationToken ct = default);
}

// Tick book for exact minute lookup (symbol + minute-aligned timestamp -> bid/ask)
public sealed class TickBook
{
    private readonly Dictionary<(string Symbol, DateTime Ts), (decimal Bid, decimal Ask)> _map = new();
    public TickBook(IEnumerable<(string Symbol, DateTime Ts, decimal Bid, decimal Ask)> rows)
    {
        foreach (var r in rows)
        {
            var key = (r.Symbol, DateTime.SpecifyKind(r.Ts, DateTimeKind.Utc));
            _map[key] = (r.Bid, r.Ask);
        }
    }
    public (decimal Bid, decimal Ask) Get(string symbol, DateTime ts)
    {
        var key = (symbol, DateTime.SpecifyKind(ts, DateTimeKind.Utc));
        if (!_map.TryGetValue(key, out var v)) throw new InvalidOperationException($"Tick not found for {symbol} @ {ts:O}");
        return v;
    }
}

public sealed class SimulatedExecutionAdapter : IExecutionAdapter
{
    private readonly TickBook _book;
    public SimulatedExecutionAdapter(TickBook book) => _book = book;

    public Task<ExecutionResult> ExecuteMarketAsync(OrderRequest order, CancellationToken ct = default)
    {
        if (order.UtcTs.Second != 0 || order.UtcTs.Millisecond != 0)
            throw new InvalidOperationException("Order timestamp must be minute-aligned (seconds==0).");
        var (bid, ask) = _book.Get(order.Symbol, order.UtcTs);
        decimal price = order.Side == TradeSide.Buy ? ask : bid;
        var fill = new ExecutionFill(order.DecisionId, order.Symbol, order.Side, price, order.Units, order.UtcTs);
        var brokerOrderId = $"STUB-{order.DecisionId}";
        return Task.FromResult(new ExecutionResult(true, string.Empty, fill, brokerOrderId));
    }
}

// Position tracking & trade finalization
public sealed class PositionTracker
{
    private sealed class OpenPosition
    {
        public string Symbol = string.Empty;
        public TradeSide Side;
        public DateTime OpenTs;
        public decimal EntryPrice;
        public long Units;
        public string DecisionId = string.Empty;
    }

    private readonly Dictionary<string, OpenPosition> _open = new();
    private readonly List<CompletedTrade> _completed = new();

    public IReadOnlyList<CompletedTrade> Completed => _completed;

    public void OnFill(ExecutionFill fill, string schemaVersion, string configHash, string sourceAdapter, string? dataVersion)
    {
        if (!_open.TryGetValue(fill.DecisionId, out var pos))
        {
            // treat as open
            _open[fill.DecisionId] = new OpenPosition
            {
                Symbol = fill.Symbol,
                Side = fill.Side,
                OpenTs = fill.UtcTs,
                EntryPrice = fill.Price,
                Units = fill.Units,
                DecisionId = fill.DecisionId
            };
            return;
        }
        // closing fill (must be opposite side)
        if (pos.Side == fill.Side)
            throw new InvalidOperationException($"Closing fill side must be opposite. Decision {fill.DecisionId}");
        // PnL: (exit - entry) * direction * units
        var dir = pos.Side == TradeSide.Buy ? 1m : -1m;
        var pnl = (fill.Price - pos.EntryPrice) * dir * pos.Units;
        var trade = new CompletedTrade(
            pos.OpenTs,
            fill.UtcTs,
            pos.Symbol,
            pos.Side,
            pos.EntryPrice,
            fill.Price,
            pos.Units,
            decimal.Round(pnl, 6, MidpointRounding.AwayFromZero),
            0m,
            pos.DecisionId,
            schemaVersion,
            configHash,
            sourceAdapter,
            dataVersion ?? string.Empty
        );
        _completed.Add(trade);
        _open.Remove(fill.DecisionId);
    }
}

public sealed record CompletedTrade(
    DateTime UtcTsOpen,
    DateTime UtcTsClose,
    string Symbol,
    TradeSide Direction,
    decimal EntryPrice,
    decimal ExitPrice,
    long VolumeUnits,
    decimal PnlCcy,
    decimal PnlR,
    string DecisionId,
    string SchemaVersion,
    string ConfigHash,
    string SourceAdapter,
    string DataVersion
);

public sealed class TradesJournalWriter : IAsyncDisposable
{
    private readonly string _path;
    private readonly List<CompletedTrade> _buffer = new();
    private readonly string _schemaVersion;
    private readonly string _configHash;
    private readonly string? _dataVersion;
    private bool _flushed;
    private readonly string _adapterId;

    public TradesJournalWriter(string journalRoot, string runId, string schemaVersion, string configHash, string adapterId, string? dataVersion)
    {
        if (string.IsNullOrWhiteSpace(adapterId)) throw new ArgumentException("Adapter id must be provided.", nameof(adapterId));
        _schemaVersion = schemaVersion;
        _configHash = configHash;
        _dataVersion = dataVersion;
        _adapterId = adapterId;
        var dir = Path.Combine(journalRoot, adapterId, runId);
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "trades.csv");
    }
    public void Append(CompletedTrade trade)
    {
        if (!string.Equals(trade.SourceAdapter, _adapterId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Trade src_adapter mismatch. Expected '{_adapterId}' but received '{trade.SourceAdapter}'.");
        _buffer.Add(trade);
    }

    public async ValueTask DisposeAsync()
    {
        if (_flushed) return;
        _flushed = true;
        var tmp = _path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
        {
            await sw.WriteLineAsync("utc_ts_open,utc_ts_close,symbol,direction,entry_price,exit_price,volume_units,pnl_ccy,pnl_r,decision_id,schema_version,config_hash,src_adapter,data_version");
            bool isM0 = _buffer.Any(bt => bt.DecisionId.StartsWith("M0-", StringComparison.Ordinal));
            // map symbol->price decimals inferred from observed entry/exit scale if needed (fallback)
            var priceDecimals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (isM0)
            {
                foreach (var t in _buffer)
                {
                    if (!priceDecimals.ContainsKey(t.Symbol))
                    {
                        // infer decimals by counting digits after decimal in entry price string representation
                        var s = t.EntryPrice.ToString(CultureInfo.InvariantCulture);
                        var idx = s.IndexOf('.');
                        priceDecimals[t.Symbol] = idx >= 0 ? s.Length - idx - 1 : 0;
                    }
                }
            }
            foreach (var t in _buffer.OrderBy(t => t.UtcTsOpen).ThenBy(t => t.Symbol))
            {
                string PriceFmt(decimal d, string symbol)
                {
                    if (!isM0) return d.ToString(CultureInfo.InvariantCulture);
                    var decs = priceDecimals.TryGetValue(symbol, out var pd) ? pd : 5;
                    return decimal.Round(d, decs).ToString($"F{decs}", CultureInfo.InvariantCulture);
                }
                string PnlFmt(decimal d)
                {
                    if (!isM0) return d.ToString(CultureInfo.InvariantCulture);
                    return decimal.Round(d, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture);
                }
                string FVol(long v) => v.ToString(CultureInfo.InvariantCulture);
                var line = string.Join(',',
                    t.UtcTsOpen.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    t.UtcTsClose.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    t.Symbol,
                    t.Direction.ToString().ToUpperInvariant(),
                    PriceFmt(t.EntryPrice, t.Symbol),
                    PriceFmt(t.ExitPrice, t.Symbol),
                    FVol(t.VolumeUnits),
                    PnlFmt(t.PnlCcy),
                    (isM0 ? "0" : t.PnlR.ToString(CultureInfo.InvariantCulture)),
                    t.DecisionId,
                    t.SchemaVersion,
                    t.ConfigHash,
                    t.SourceAdapter,
                    t.DataVersion
                );
                await sw.WriteLineAsync(line);
            }
        }
        if (File.Exists(_path)) File.Delete(_path);
        File.Move(tmp, _path);
    }
}
