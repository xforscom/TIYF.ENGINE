using TiYf.Engine.Core;

namespace TiYf.Engine.Sim;

/// <summary>
/// Deterministic M0 strategy: for each configured instrument emits exactly two market orders:
/// BUY at start + 15 minutes, SELL at start + 75 minutes. Each position is closed 30 minutes after open.
/// DecisionIds: M0-<SYMBOL>-01 (first), M0-<SYMBOL>-02 (second).
/// No randomness, no indicators; relies only on IClock timestamps.
/// This file only defines scheduling logic; actual order/risk/journaling integration will be added when
/// trades journaling is implemented (next todos). For now we surface scheduled actions for unit tests.
/// </summary>
public sealed class DeterministicScriptStrategy
{
    private readonly IClock _clock;
    private readonly IReadOnlyList<Instrument> _instruments;
    private readonly DateTime _startUtc; // derived from first clock timestamp
    private readonly TimeSpan _openHold = TimeSpan.FromMinutes(30);
    private readonly Dictionary<string, List<ScheduledAction>> _actionsBySymbol = new();

    public DeterministicScriptStrategy(IClock clock, IEnumerable<Instrument> instruments, DateTime startUtc)
    {
        _clock = clock;
        _instruments = instruments.ToList();
        _startUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        BuildSchedules();
    }

    private void BuildSchedules()
    {
        foreach (var inst in _instruments)
        {
            var list = new List<ScheduledAction>();
            // Action 1 BUY
            var tBuy = _startUtc.AddMinutes(15);
            list.Add(new ScheduledAction(inst.Symbol, 1, tBuy, Side.Buy, MakeDecisionId(inst.Symbol, 1)));
            // Close of BUY
            list.Add(new ScheduledAction(inst.Symbol, 1, tBuy.Add(_openHold), Side.Close, MakeDecisionId(inst.Symbol, 1)));
            // Action 2 SELL
            var tSell = _startUtc.AddMinutes(75);
            list.Add(new ScheduledAction(inst.Symbol, 2, tSell, Side.Sell, MakeDecisionId(inst.Symbol, 2)));
            // Close of SELL
            list.Add(new ScheduledAction(inst.Symbol, 2, tSell.Add(_openHold), Side.Close, MakeDecisionId(inst.Symbol, 2)));
            _actionsBySymbol[inst.Symbol] = list;
        }
    }

    private static string MakeDecisionId(string symbol, int ordinal) => $"M0-{symbol}-{ordinal:00}";

    public IEnumerable<ScheduledAction> Pending(DateTime nowUtc)
    {
        // Return any actions whose timestamp == current minute aligned time (no lookahead)
        foreach (var kv in _actionsBySymbol)
        {
            var list = kv.Value;
            foreach (var act in list.Where(a => !a.Emitted && a.WhenUtc == nowUtc))
            {
                act.Emitted = true;
                yield return act;
            }
        }
    }

    public IReadOnlyDictionary<string, List<ScheduledAction>> DebugAll() => _actionsBySymbol;

    public sealed class ScheduledAction
    {
        public string Symbol { get; }
        public int Ordinal { get; }
        public DateTime WhenUtc { get; }
        public Side Side { get; }
        public string DecisionId { get; }
        public bool Emitted { get; set; }
        public ScheduledAction(string symbol, int ordinal, DateTime whenUtc, Side side, string decisionId)
        {
            Symbol = symbol; Ordinal = ordinal; WhenUtc = DateTime.SpecifyKind(whenUtc, DateTimeKind.Utc); Side = side; DecisionId = decisionId;
        }
        public override string ToString() => $"{Symbol}#{Ordinal}:{Side}@{WhenUtc:O} ({DecisionId})";
    }
}

public enum Side { Buy, Sell, Close }
