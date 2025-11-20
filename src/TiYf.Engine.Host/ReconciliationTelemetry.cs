using Microsoft.Extensions.Logging;
using TiYf.Engine.Sim;

namespace TiYf.Engine.Host;

internal sealed class ReconciliationTelemetry
{
    private readonly Func<IReadOnlyCollection<(string Symbol, TradeSide Side, decimal EntryPrice, long Units, DateTime OpenTimestamp)>> _engineSnapshotProvider;
    private readonly Func<CancellationToken, Task<BrokerAccountSnapshot?>> _brokerSnapshotProvider;
    private readonly ReconciliationJournalWriter _journalWriter;
    private readonly EngineHostState _state;
    private readonly ILogger _logger;

    public ReconciliationTelemetry(
        Func<IReadOnlyCollection<(string Symbol, TradeSide Side, decimal EntryPrice, long Units, DateTime OpenTimestamp)>> engineSnapshotProvider,
        Func<CancellationToken, Task<BrokerAccountSnapshot?>> brokerSnapshotProvider,
        ReconciliationJournalWriter journalWriter,
        EngineHostState state,
        ILogger logger)
    {
        _engineSnapshotProvider = engineSnapshotProvider ?? throw new ArgumentNullException(nameof(engineSnapshotProvider));
        _brokerSnapshotProvider = brokerSnapshotProvider ?? (_ => Task.FromResult<BrokerAccountSnapshot?>(null));
        _journalWriter = journalWriter ?? throw new ArgumentNullException(nameof(journalWriter));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EmitAsync(DateTime utcNow, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        IReadOnlyCollection<(string Symbol, TradeSide Side, decimal EntryPrice, long Units, DateTime OpenTimestamp)> enginePositions;
        try
        {
            enginePositions = _engineSnapshotProvider() ?? Array.Empty<(string, TradeSide, decimal, long, DateTime)>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture engine position snapshot for reconciliation");
            enginePositions = Array.Empty<(string, TradeSide, decimal, long, DateTime)>();
        }

        BrokerAccountSnapshot? brokerSnapshot = null;
        try
        {
            brokerSnapshot = await _brokerSnapshotProvider(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture broker snapshot for reconciliation");
        }

        var records = ReconciliationRecordBuilder.Build(utcNow, enginePositions, brokerSnapshot);
        var mismatches = 0L;
        var aggregate = ReconciliationStatus.Match;
        if ((brokerSnapshot?.Orders?.Count ?? 0) > 0 && enginePositions.Count == 0)
        {
            mismatches += brokerSnapshot!.Orders.Count;
            aggregate = ReconciliationStatus.Mismatch;
        }

        foreach (var record in records)
        {
            if (record.Status == ReconciliationStatus.Mismatch)
            {
                mismatches++;
            }

            aggregate = MaxStatus(aggregate, record.Status);
            await _journalWriter.AppendAsync(record, ct).ConfigureAwait(false);
        }

        var duration = (DateTime.UtcNow - start).TotalSeconds;
        _state.RecordReconciliationTelemetry(aggregate, mismatches, utcNow, duration);
    }

    private static ReconciliationStatus MaxStatus(ReconciliationStatus a, ReconciliationStatus b)
        => (ReconciliationStatus)Math.Max((int)a, (int)b);
}
