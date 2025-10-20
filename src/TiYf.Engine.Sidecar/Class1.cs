using System;
using System.Text;
using System.Text.Json;
using TiYf.Engine.Core;

namespace TiYf.Engine.Sidecar;

public sealed class FileJournalWriter : IJournalWriter
{
    private readonly StreamWriter _writer;
    private bool _disposed;
    private ulong _nextSeq = 1UL;
    public string Path { get; }
    public string RunDirectory { get; }
    private readonly string _sourceAdapter;

    public FileJournalWriter(
        string directory,
        string runId,
        string schemaVersion,
        string configHash,
        string adapterId,
        string brokerId,
        string accountId,
        string? dataVersion = null)
    {
        if (string.IsNullOrWhiteSpace(adapterId)) throw new ArgumentException("Adapter id must be provided.", nameof(adapterId));
        Directory.CreateDirectory(directory);
        RunDirectory = System.IO.Path.Combine(directory, adapterId, runId);
        Path = System.IO.Path.Combine(RunDirectory, "events.csv");
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(dir);
        var exists = File.Exists(Path);
        _writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
        _sourceAdapter = adapterId;
        if (!exists)
        {
            var meta = $"schema_version={schemaVersion},config_hash={configHash},adapter_id={adapterId},broker={brokerId},account_id={accountId}";
            if (!string.IsNullOrWhiteSpace(dataVersion)) meta += $",data_version={dataVersion}";
            _writer.WriteLine(meta);
            _writer.WriteLine("sequence,utc_ts,event_type,src_adapter,payload_json");
            _writer.Flush();
        }
    }

    public async Task AppendAsync(JournalEvent evt, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FileJournalWriter));
        // If caller passed Sequence=0 treat as assign-next
        var seq = evt.Sequence == 0 ? _nextSeq : evt.Sequence;
        if (seq != _nextSeq) throw new InvalidOperationException("Non-monotonic sequence usage");
        _nextSeq++;
        if (!string.Equals(evt.SourceAdapter, _sourceAdapter, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Journal event src_adapter mismatch. Expected '{_sourceAdapter}', received '{evt.SourceAdapter}'.");
        var line = new JournalEvent(seq, evt.UtcTimestamp, evt.EventType, _sourceAdapter, evt.Payload).ToCsvLine();
        await _writer.WriteLineAsync(line);
        await _writer.FlushAsync();
    }

    public async Task AppendRangeAsync(IEnumerable<JournalEvent> events, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FileJournalWriter));
        var sb = new StringBuilder();
        foreach (var evt in events)
        {
            var seq = evt.Sequence == 0 ? _nextSeq : evt.Sequence;
            if (seq != _nextSeq) throw new InvalidOperationException("Non-monotonic sequence in batch");
            _nextSeq++;
            if (!string.Equals(evt.SourceAdapter, _sourceAdapter, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Journal event src_adapter mismatch. Expected '{_sourceAdapter}', received '{evt.SourceAdapter}'.");
            sb.AppendLine(new JournalEvent(seq, evt.UtcTimestamp, evt.EventType, _sourceAdapter, evt.Payload).ToCsvLine());
        }
        await _writer.WriteAsync(sb.ToString());
        await _writer.FlushAsync();
    }

    public ulong NextSequence => _nextSeq;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _writer.FlushAsync();
        _writer.Dispose();
    }
}

public static class SampleDataSeeder
{
    public static void EnsureSample(string root)
    {
        var instrPath = System.IO.Path.Combine(root, "sample-instruments.csv");
        if (!File.Exists(instrPath))
        {
            File.WriteAllText(instrPath, "id,symbol,decimals\nINST1,FOO,2\n");
        }
        var ticksPath = System.IO.Path.Combine(root, "sample-ticks.csv");
        if (!File.Exists(ticksPath))
        {
            // Two minutes of synthetic ticks
            var now = DateTime.UtcNow;
            var minute0 = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            var minute1 = minute0.AddMinutes(1);
            var sb = new StringBuilder();
            sb.AppendLine("utc_ts,price,volume");
            sb.AppendLine($"{minute0:O},100.0,1");
            sb.AppendLine($"{minute0.AddSeconds(15):O},101.0,2");
            sb.AppendLine($"{minute0.AddSeconds(30):O},99.5,1.5");
            sb.AppendLine($"{minute0.AddSeconds(45):O},100.5,1");
            sb.AppendLine($"{minute1:O},100.2,1");
            sb.AppendLine($"{minute1.AddSeconds(10):O},100.8,2");
            File.WriteAllText(ticksPath, sb.ToString());
        }
    }
}
