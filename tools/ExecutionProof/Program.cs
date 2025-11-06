using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TiYf.Engine.Core;
using TiYf.Engine.Core.Slippage;
using TiYf.Engine.Host;
using TiYf.Engine.Sim;

const string CaseRetry = "retry_transient";
const string CaseReject = "reject_permanent";
const string CasePartial = "partial_fills";
const string CaseKillSwitch = "kill_switch";
var proofClock = new DateTime(2025, 5, 1, 10, 0, 0, DateTimeKind.Utc);
var outputDir = ParseOutputDirectory(args);
Console.WriteLine($"Using output directory: {outputDir}");
Directory.CreateDirectory(outputDir);

var eventLog = new List<(string Case, JournalEvent Event)>();
var tradesLog = new List<(string Case, CompletedTrade Trade)>();
var summary = new List<object>();

// Case: transient failure then success
{
    var adapter = new ToolExecutionAdapter(
        _ => new ExecutionResult(false, "timeout", null, null, 504, true),
        order => ExecutionResultSuccess(order, order.PriceIntent ?? 0m));
    var journal = new ToolJournalWriter();
    var loop = CreateLoop(adapter, journal, utcNow: () => proofClock);
    var order = new OrderRequest("CASE-RETRY-1", "EURUSD", TradeSide.Buy, 100, proofClock, null);
    var outcome = await InvokeDispatchAsync(loop, order, "H1", false, 1.2000m, 1.2000m);
    summary.Add(new
    {
        @case = CaseRetry,
        status = GetStatus(outcome),
        attempts = adapter.Requests.Count,
        events = journal.Events.Count
    });
    foreach (var evt in journal.Events)
    {
        eventLog.Add((CaseRetry, evt));
    }
}

// Case: permanent reject
{
    var adapter = new ToolExecutionAdapter(
        _ => new ExecutionResult(false, "validation_error", null, null, 422, false));
    var journal = new ToolJournalWriter();
    var loop = CreateLoop(adapter, journal, utcNow: () => proofClock.AddMinutes(1));
    var order = new OrderRequest("CASE-REJECT-1", "EURUSD", TradeSide.Buy, 120, proofClock.AddMinutes(1), null);
    var outcome = await InvokeDispatchAsync(loop, order, "H1", false, 1.1100m, 1.1100m);
    summary.Add(new
    {
        @case = CaseReject,
        status = GetStatus(outcome),
        attempts = adapter.Requests.Count,
        events = journal.Events.Count
    });
    foreach (var evt in journal.Events)
    {
        eventLog.Add((CaseReject, evt));
    }
}

// Case: partial fill aggregation
{
    var tracker = new PositionTracker();
    tracker.OnFill(new ExecutionFill("CASE-PARTIAL-1", "XAUUSD", TradeSide.Buy, 1950.00m, 3, proofClock.AddMinutes(2)), "1.2.0", "hash", "adapter", null);
    tracker.OnFill(new ExecutionFill("CASE-PARTIAL-1", "XAUUSD", TradeSide.Buy, 1952.00m, 2, proofClock.AddMinutes(3)), "1.2.0", "hash", "adapter", null);
    tracker.OnFill(new ExecutionFill("CASE-PARTIAL-1", "XAUUSD", TradeSide.Sell, 1958.50m, 3, proofClock.AddMinutes(4)), "1.2.0", "hash", "adapter", null);
    tracker.OnFill(new ExecutionFill("CASE-PARTIAL-1", "XAUUSD", TradeSide.Sell, 1960.00m, 2, proofClock.AddMinutes(5)), "1.2.0", "hash", "adapter", null);
    foreach (var trade in tracker.Completed)
    {
        tradesLog.Add((CasePartial, trade));
    }
    summary.Add(new
    {
        @case = CasePartial,
        trade_count = tracker.Completed.Count,
        pnl_total = tracker.Completed.Sum(t => t.PnlCcy)
    });
}

// Case: kill-switch throttling
{
    var previous = Environment.GetEnvironmentVariable("TIYF_KILLSWITCH");
    Environment.SetEnvironmentVariable("TIYF_KILLSWITCH", "1");
    try
    {
        var adapter = new ToolExecutionAdapter(order => ExecutionResultSuccess(order, order.PriceIntent ?? 0m));
        var journal = new ToolJournalWriter();
        var currentTime = proofClock.AddMinutes(10);
        var loop = CreateLoop(adapter, journal, utcNow: () => currentTime);

        var entryStatuses = new List<string>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt == 1)
            {
                currentTime = currentTime.AddSeconds(30);
            }
            else if (attempt == 2)
            {
                currentTime = currentTime.AddMinutes(1);
            }
            var decisionId = $"CASE-KS-ENTRY-{attempt + 1}";
            var order = new OrderRequest(decisionId, "EURUSD", TradeSide.Buy, 50, currentTime, null);
            var outcome = await InvokeDispatchAsync(loop, order, "H1", false, 1.2050m, 1.2050m);
            entryStatuses.Add(GetStatus(outcome));
        }

        currentTime = currentTime.AddMinutes(1);
        var exitOrder = new OrderRequest("CASE-KS-EXIT-1", "EURUSD", TradeSide.Sell, 50, currentTime, null);
        var exitOutcome = await InvokeDispatchAsync(loop, exitOrder, "H1", true, 1.1980m, 1.1980m);

        summary.Add(new
        {
            @case = CaseKillSwitch,
            entries_blocked = entryStatuses.Count(status => string.Equals(status, "BlockedKillSwitch", StringComparison.OrdinalIgnoreCase)),
            exit_status = GetStatus(exitOutcome),
            alert_count = journal.Events.Count
        });

        foreach (var evt in journal.Events)
        {
            eventLog.Add((CaseKillSwitch, evt));
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable("TIYF_KILLSWITCH", previous);
    }
}

// Write artifacts
WriteEventsCsv(Path.Combine(outputDir, "events.csv"), eventLog);
WriteTradesCsv(Path.Combine(outputDir, "trades.csv"), tradesLog);
await WriteSummaryAsync(Path.Combine(outputDir, "summary.json"), summary);
WriteMetricsArtifacts(outputDir, eventLog.Count, tradesLog.Count);

Console.WriteLine($"Proof artifacts written to '{outputDir}'.");
return 0;

// Helpers

string ParseOutputDirectory(string[] arguments)
{
    for (var i = 0; i < arguments.Length; i++)
    {
        if (arguments[i] == "--output" && i + 1 < arguments.Length)
        {
            var candidate = arguments[i + 1];
            if (Path.IsPathRooted(candidate))
            {
                return candidate;
            }
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, candidate));
        }
    }
    return Path.Combine(Path.GetTempPath(), $"execution-proof-{Guid.NewGuid():N}");
}

EngineLoop CreateLoop(
    IExecutionAdapter adapter,
    ToolJournalWriter journal,
    RiskConfig? riskConfig = null,
    Func<DateTime>? utcNow = null)
{
    var clock = new DeterministicSequenceClock(new[] { proofClock });
    return new EngineLoop(
        clock,
        new Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder>(),
        new InMemoryBarKeyTracker(),
        journal,
        new EmptyTickSource(),
        "BAR_V1",
        execution: adapter,
        positions: new PositionTracker(),
        riskConfig: riskConfig,
        sourceAdapter: "proof",
        slippageModel: new PassThroughSlippageModel(),
        utcNow: utcNow ?? (() => DateTime.UtcNow));
}

async Task<object> InvokeDispatchAsync(
    EngineLoop loop,
    OrderRequest order,
    string timeframe,
    bool isExit,
    decimal intentPrice,
    decimal slippedPrice)
{
    var method = typeof(EngineLoop).GetMethod("DispatchOrderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var taskObj = method.Invoke(loop, new object[] { order, timeframe, isExit, intentPrice, slippedPrice, CancellationToken.None })!;
    var task = (Task)taskObj;
    await task.ConfigureAwait(false);
    return taskObj.GetType().GetProperty("Result")!.GetValue(taskObj)!;
}

string GetStatus(object outcome)
    => outcome.GetType().GetProperty("Status")!.GetValue(outcome)!.ToString()!;

ExecutionResult ExecutionResultSuccess(OrderRequest order, decimal price) =>
    new ExecutionResult(true, string.Empty, new ExecutionFill(order.DecisionId, order.Symbol, order.Side, price, order.Units, order.UtcTs), "BROKER-OK");

void WriteEventsCsv(string path, IEnumerable<(string Case, JournalEvent Event)> events)
{
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }
    var builder = new StringBuilder();
    builder.AppendLine("case,event_type,decision_id,reason,ts_iso,payload_json");
    foreach (var (caseName, evt) in events)
    {
        var payloadJson = JsonSerializer.Serialize(evt.Payload);
        var reason = evt.Payload.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? string.Empty : string.Empty;
        builder.Append(caseName).Append(',')
            .Append(evt.EventType).Append(',')
            .Append(evt.Payload.TryGetProperty("decision_id", out var idEl) ? idEl.GetString() ?? string.Empty : evt.EventType.Contains("PARTIAL", StringComparison.OrdinalIgnoreCase) ? "n/a" : string.Empty).Append(',')
            .Append(reason).Append(',')
            .Append(evt.UtcTimestamp.ToString("O", CultureInfo.InvariantCulture)).Append(',')
            .Append(payloadJson.Replace(Environment.NewLine, string.Empty));
        builder.AppendLine();
    }
    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    using var writer = new StreamWriter(stream, Encoding.UTF8);
    writer.Write(builder.ToString());
}

void WriteTradesCsv(string path, IEnumerable<(string Case, CompletedTrade Trade)> trades)
{
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }
    var builder = new StringBuilder();
    builder.AppendLine("case,decision_id,symbol,entry_price,exit_price,units,pnl_ccy");
    foreach (var (caseName, trade) in trades)
    {
        builder.Append(caseName).Append(',')
            .Append(trade.DecisionId).Append(',')
            .Append(trade.Symbol).Append(',')
            .Append(trade.EntryPrice.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(trade.ExitPrice.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(trade.VolumeUnits.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(trade.PnlCcy.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
    }
    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    using var writer = new StreamWriter(stream, Encoding.UTF8);
    writer.Write(builder.ToString());
}

async Task WriteSummaryAsync(string path, IEnumerable<object> entries)
{
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }
    var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
    using var writer = new StreamWriter(stream, Encoding.UTF8);
    await writer.WriteAsync(json);
}

void WriteMetricsArtifacts(string outputDir, int eventCount, int tradeCount)
{
    var state = new EngineHostState("proof", Array.Empty<string>());
    state.MarkConnected(true);
    state.SetLoopStart(proofClock);
    state.SetTimeframes(new[] { "H1" });
    state.SetMetrics(openPositions: tradeCount, activeOrders: 0, riskEventsTotal: eventCount, alertsTotal: eventCount);
    state.RegisterOrderAccepted("EURUSD", 100);
    if (eventCount > 0)
    {
        state.RegisterOrderRejected();
    }
    state.UpdateIdempotencyMetrics(orderCacheSize: 3, cancelCacheSize: 1, evictionsTotal: 2);
    state.SetSlippageModel("zero");
    var metricsText = EngineMetricsFormatter.Format(state.CreateMetricsSnapshot());
    File.WriteAllText(Path.Combine(outputDir, "metrics.txt"), metricsText);

    var healthJson = JsonSerializer.Serialize(state.CreateHealthPayload(), new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(outputDir, "health.json"), healthJson);
}


// Local helper classes

sealed class EmptyTickSource : ITickSource
{
    public IEnumerator<PriceTick> GetEnumerator() => Enumerable.Empty<PriceTick>().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

sealed class PassThroughSlippageModel : ISlippageModel
{
    public decimal Apply(decimal intendedPrice, bool isBuy, string instrumentId, long units, DateTime utcNow)
        => intendedPrice;
}

sealed class ToolExecutionAdapter : IExecutionAdapter
{
    private readonly Queue<Func<OrderRequest, ExecutionResult>> _behaviors;
    private Func<OrderRequest, ExecutionResult> _fallback;

    public ToolExecutionAdapter(params Func<OrderRequest, ExecutionResult>[] behaviors)
    {
        if (behaviors == null || behaviors.Length == 0)
        {
            throw new ArgumentException("At least one behavior must be provided.", nameof(behaviors));
        }
        _behaviors = new Queue<Func<OrderRequest, ExecutionResult>>(behaviors);
        _fallback = behaviors[^1];
    }

    public List<OrderRequest> Requests { get; } = new();

    public Task<ExecutionResult> ExecuteMarketAsync(OrderRequest order, CancellationToken ct = default)
    {
        Requests.Add(order);
        var behavior = _behaviors.Count > 0 ? _behaviors.Dequeue() : _fallback;
        var result = behavior(order);
        if (_behaviors.Count == 0)
        {
            _fallback = behavior;
        }
        return Task.FromResult(result);
    }
}

sealed class ToolJournalWriter : IJournalWriter
{
    public List<JournalEvent> Events { get; } = new();

    public Task AppendAsync(JournalEvent evt, CancellationToken ct = default)
    {
        Events.Add(evt);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
