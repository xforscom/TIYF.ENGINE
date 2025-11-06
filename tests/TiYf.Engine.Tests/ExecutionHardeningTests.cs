using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TiYf.Engine.Core;
using TiYf.Engine.Core.Slippage;
using TiYf.Engine.Sim;
using Xunit;

namespace TiYf.Engine.Tests;

public class ExecutionHardeningTests
{
    private static readonly DateTime BaseTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Dispatch_IdempotentSkipsDuplicateSend()
    {
        var adapter = new TestExecutionAdapter(
            order => ExecutionResultSuccess(order, order.PriceIntent ?? 0m));
        var journal = new TestJournalWriter();
        var loop = CreateLoop(adapter, journal, utcNow: () => BaseTime);

        var order = new OrderRequest("DEC-1", "EURUSD", TradeSide.Buy, 100, BaseTime, null);
        var firstOutcome = await InvokeDispatchAsync(loop, order, "H1", isExit: false, 1.2000m, 1.2005m);
        Assert.Equal("Accepted", GetStatus(firstOutcome));
        Assert.Single(adapter.Requests);
        Assert.Equal(1.2005m, adapter.Requests[0].PriceIntent);

        var duplicateOutcome = await InvokeDispatchAsync(loop, order, "H1", isExit: false, 1.2000m, 1.2005m);
        Assert.Equal("Duplicate", GetStatus(duplicateOutcome));
        Assert.Single(adapter.Requests);
        Assert.Empty(journal.Events);
    }

    [Fact]
    public async Task Dispatch_KillSwitchBlocksEntries_AllowsExits()
    {
        const string killEnv = "TIYF_KILLSWITCH";
        var previous = Environment.GetEnvironmentVariable(killEnv);
        try
        {
            Environment.SetEnvironmentVariable(killEnv, "1");
            var adapter = new TestExecutionAdapter(
                order => ExecutionResultSuccess(order, order.PriceIntent ?? 0m));
            var journal = new TestJournalWriter();
            var now = BaseTime;
            var loop = CreateLoop(adapter, journal, utcNow: () => now);

            var order = new OrderRequest("DEC-2", "EURUSD", TradeSide.Buy, 100, now, null);
            var blocked = await InvokeDispatchAsync(loop, order, "H1", isExit: false, 1.1000m, 1.1000m);
            Assert.Equal("BlockedKillSwitch", GetStatus(blocked));
            Assert.Empty(adapter.Requests);
            Assert.Single(journal.Events);
            Assert.Equal("ALERT_KILLSWITCH", journal.Events[0].EventType);

            var secondAttempt = await InvokeDispatchAsync(loop, order, "H1", isExit: false, 1.1000m, 1.1000m);
            Assert.Equal("BlockedKillSwitch", GetStatus(secondAttempt));
            Assert.Single(journal.Events); // dedup within window

            var exitOrder = new OrderRequest("DEC-2", "EURUSD", TradeSide.Sell, 80, now, null);
            var exitOutcome = await InvokeDispatchAsync(loop, exitOrder, "H1", isExit: true, 1.1000m, 1.1000m);
            Assert.Equal("Accepted", GetStatus(exitOutcome));
            Assert.Single(adapter.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable(killEnv, previous);
        }
    }

    [Fact]
    public async Task Dispatch_SizeLimitBlocksAndEmitsAlert()
    {
        var adapter = new TestExecutionAdapter(
            order => ExecutionResultSuccess(order, order.PriceIntent ?? 0m));
        var journal = new TestJournalWriter();
        var riskConfig = new RiskConfig
        {
            MaxUnitsPerSymbol = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["EURUSD"] = 100
            }
        };
        var loop = CreateLoop(adapter, journal, riskConfig: riskConfig, utcNow: () => BaseTime);

        var order = new OrderRequest("DEC-3", "EURUSD", TradeSide.Buy, 150, BaseTime, null);
        var outcome = await InvokeDispatchAsync(loop, order, "H1", isExit: false, 1.0500m, 1.0500m);
        Assert.Equal("BlockedSizeLimit", GetStatus(outcome));
        Assert.Empty(adapter.Requests);
        Assert.Single(journal.Events);
        var alert = journal.Events[0];
        Assert.Equal("ALERT_SIZE_LIMIT", alert.EventType);
        Assert.Equal("test-adapter", alert.SourceAdapter);
        Assert.Equal(150, alert.Payload.GetProperty("requested_units").GetInt64());
        Assert.Equal(100, alert.Payload.GetProperty("max_units").GetInt64());
        Assert.Equal(1.0500m, alert.Payload.GetProperty("price_intent").GetDecimal());
    }

    [Fact]
    public async Task Dispatch_AdapterRejectsEmitAlertAndCounter()
    {
        var rejections = 0;
        var adapter = new TestExecutionAdapter(_ => new ExecutionResult(
            Accepted: false,
            Reason: "validation_error",
            Fill: null,
            BrokerOrderId: null,
            StatusCode: 422,
            Transient: false));
        var journal = new TestJournalWriter();
        var loop = CreateLoop(
            adapter,
            journal,
            utcNow: () => BaseTime,
            onOrderRejected: () => Interlocked.Increment(ref rejections));

        var order = new OrderRequest("DEC-4", "EURUSD", TradeSide.Buy, 100, BaseTime, null);
        var outcome = await InvokeDispatchAsync(loop, order, "H1", isExit: false, 1.0700m, 1.0700m);
        Assert.Equal("Rejected", GetStatus(outcome));
        Assert.Single(adapter.Requests);
        Assert.Equal(1, rejections);
        Assert.Single(journal.Events);
        var evt = journal.Events[0];
        Assert.Equal("ALERT_ORDER_REJECTED", evt.EventType);
        Assert.Equal("validation_error", evt.Payload.GetProperty("reason").GetString());
        Assert.Equal(422, evt.Payload.GetProperty("adapter_http_status").GetInt32());
    }

    [Fact]
    public void PositionTracker_AggregatesPartialFills()
    {
        var tracker = new PositionTracker();
        var openTs = BaseTime;
        tracker.OnFill(new ExecutionFill("DEC-5", "EURUSD", TradeSide.Buy, 1.0000m, 600, openTs), "1.2.0", "hash", "adapter", null);
        tracker.OnFill(new ExecutionFill("DEC-5", "EURUSD", TradeSide.Buy, 1.0200m, 400, openTs.AddMinutes(1)), "1.2.0", "hash", "adapter", null);

        var snapshot = tracker.SnapshotOpenPositions().Single();
        Assert.Equal(1000, snapshot.Units);
        Assert.Equal(1.008m, snapshot.EntryPrice);

        tracker.OnFill(new ExecutionFill("DEC-5", "EURUSD", TradeSide.Sell, 1.0500m, 600, openTs.AddMinutes(2)), "1.2.0", "hash", "adapter", null);
        Assert.Equal(400, tracker.GetOpenUnits("DEC-5"));
        Assert.Empty(tracker.Completed);

        tracker.OnFill(new ExecutionFill("DEC-5", "EURUSD", TradeSide.Sell, 1.0600m, 400, openTs.AddMinutes(3)), "1.2.0", "hash", "adapter", null);
        Assert.Equal(0, tracker.GetOpenUnits("DEC-5"));
        var trade = tracker.Completed.Single();
        Assert.Equal(1.008m, trade.EntryPrice);
        Assert.Equal(1.054m, trade.ExitPrice);
        Assert.Equal(1000, trade.VolumeUnits);
        Assert.Equal(46.0m, trade.PnlCcy);
    }

    [Fact]
    public async Task IdempotencyCache_WarnsOnceAndReportsEvictions()
    {
        var adapter = new TestExecutionAdapter(order => ExecutionResultSuccess(order, order.PriceIntent ?? 0m));
        var journal = new TestJournalWriter();
        var metrics = new List<(int Order, int Cancel, long Evictions)>();
        var warnings = new List<string>();
        var currentTime = BaseTime;
        var loop = CreateLoop(
            adapter,
            journal,
            utcNow: () => currentTime,
            onIdempotencyMetrics: (orderSize, cancelSize, evictions) => metrics.Add((orderSize, cancelSize, evictions)),
            onWarn: message => warnings.Add(message));

        const int totalOrders = 5050;
        for (var i = 0; i < totalOrders; i++)
        {
            currentTime = BaseTime.AddSeconds(i);
            var orderTime = currentTime;
            var decisionId = $"DEC-IDEMP-{i}";
            var order = new OrderRequest(decisionId, "EURUSD", TradeSide.Buy, 100, orderTime, null);
            await InvokeDispatchAsync(loop, order, "H1", isExit: false, 1.2000m, 1.2000m);
        }

        Assert.Single(warnings);
        Assert.Contains("idempotency", warnings[0], StringComparison.OrdinalIgnoreCase);
        var last = metrics.Last();
        Assert.Equal(0, last.Cancel);
        var expectedEvictions = totalOrders - last.Order;
        Assert.True(expectedEvictions > 0);
        Assert.Equal(expectedEvictions, last.Evictions);
    }

    private static EngineLoop CreateLoop(
        IExecutionAdapter adapter,
        TestJournalWriter journal,
        RiskConfig? riskConfig = null,
        Func<DateTime>? utcNow = null,
        Action<string, long>? onOrderAccepted = null,
        Action? onOrderRejected = null,
        Action<int, int, long>? onIdempotencyMetrics = null,
        Action<string>? onWarn = null)
    {
        var clock = new DeterministicSequenceClock(new[] { BaseTime });
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
            sourceAdapter: "test-adapter",
            slippageModel: new PassThroughSlippageModel(),
            utcNow: utcNow ?? (() => DateTime.UtcNow),
            orderAcceptedCallback: onOrderAccepted,
            orderRejectedCallback: onOrderRejected,
            idempotencyMetricsCallback: onIdempotencyMetrics,
            warnCallback: onWarn);
    }

    private static ExecutionResult ExecutionResultSuccess(OrderRequest order, decimal price) =>
        new ExecutionResult(true, string.Empty, new ExecutionFill(order.DecisionId, order.Symbol, order.Side, price, order.Units, order.UtcTs), "BROKER-OK");

    private static async Task<object> InvokeDispatchAsync(
        EngineLoop loop,
        OrderRequest order,
        string timeframe,
        bool isExit,
        decimal intentPrice,
        decimal slippedPrice)
    {
        var method = typeof(EngineLoop)
            .GetMethod("DispatchOrderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var taskObj = method.Invoke(loop, new object[] { order, timeframe, isExit, intentPrice, slippedPrice, CancellationToken.None })!;
        var task = (Task)taskObj;
        await task.ConfigureAwait(false);
        return taskObj.GetType().GetProperty("Result")!.GetValue(taskObj)!;
    }

    private static string GetStatus(object outcome)
        => outcome.GetType().GetProperty("Status")!.GetValue(outcome)!.ToString()!;

    private sealed class EmptyTickSource : ITickSource
    {
        public IEnumerator<PriceTick> GetEnumerator() => Enumerable.Empty<PriceTick>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class PassThroughSlippageModel : ISlippageModel
    {
        public decimal Apply(decimal intendedPrice, bool isBuy, string instrumentId, long units, DateTime utcNow)
            => intendedPrice;
    }

    private sealed class TestExecutionAdapter : IExecutionAdapter
    {
        private readonly Queue<Func<OrderRequest, ExecutionResult>> _behaviors;
        private Func<OrderRequest, ExecutionResult> _fallback;

        public TestExecutionAdapter(params Func<OrderRequest, ExecutionResult>[] behaviors)
        {
            if (behaviors is null || behaviors.Length == 0)
            {
                throw new ArgumentException("Behaviors must be provided.", nameof(behaviors));
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

    private sealed class TestJournalWriter : IJournalWriter
    {
        public List<JournalEvent> Events { get; } = new();

        public Task AppendAsync(JournalEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
