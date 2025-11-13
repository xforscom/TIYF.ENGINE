using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiYf.Engine.Host;

internal sealed class ReconciliationJournalWriter : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string _root;
    private readonly string _adapter;
    private readonly string _runId;
    private readonly string _configHash;
    private readonly string _accountId;
    private readonly SemaphoreSlim _sync = new(1, 1);

    private DateTime _currentDate = DateTime.MinValue;
    private StreamWriter? _writer;
    private string? _currentPath;

    public ReconciliationJournalWriter(string root, string adapter, string runId, string configHash, string accountId)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _adapter = adapter ?? "unknown";
        _runId = string.IsNullOrWhiteSpace(runId) ? "unknown" : runId;
        _configHash = configHash ?? string.Empty;
        _accountId = string.IsNullOrWhiteSpace(accountId) ? "unknown" : accountId;
    }

    public async Task AppendAsync(ReconciliationRecord record, CancellationToken ct = default)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureWriterAsync(record.UtcTimestamp, ct).ConfigureAwait(false);
            if (_writer is null)
            {
                return;
            }

            var payload = new
            {
                schema = "reconcile.v1",
                run_id = _runId,
                adapter = _adapter,
                account_id = _accountId,
                config_hash = _configHash,
                utc_ts = record.UtcTimestamp,
                symbol = record.Symbol,
                status = record.Status.ToString().ToLowerInvariant(),
                reason = record.Reason,
                engine_position = record.EnginePosition,
                broker_position = record.BrokerPosition,
                engine_orders = record.EngineOrders,
                broker_orders = record.BrokerOrders
            };

            var json = JsonSerializer.Serialize(payload, SerializerOptions);
            await _writer.WriteLineAsync(json).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task EnsureWriterAsync(DateTime timestamp, CancellationToken ct)
    {
        var date = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc).Date;
        if (_writer is not null && date == _currentDate)
        {
            return;
        }

        _currentDate = date;
        await DisposeWriterAsync().ConfigureAwait(false);

        var dir = Path.Combine(_root, _adapter, _runId, date.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);
        _currentPath = Path.Combine(dir, "reconcile.jsonl");
        var exists = File.Exists(_currentPath);
        _writer = new StreamWriter(new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        if (!exists)
        {
            var header = $"# schema=reconcile.v1 adapter={_adapter} run_id={_runId} account={_accountId} config_hash={_configHash}";
            await _writer.WriteLineAsync(header).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeWriterAsync()
    {
        if (_writer is null)
        {
            return;
        }

        await _writer.FlushAsync().ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writer = null;
        _currentPath = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeWriterAsync().ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
        _sync.Dispose();
    }

    public string? CurrentPath => _currentPath;
}
