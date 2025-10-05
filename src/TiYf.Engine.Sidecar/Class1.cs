using System.Text;
using System.Text.Json;
using TiYf.Engine.Core;

namespace TiYf.Engine.Sidecar;

public sealed class FileJournalWriter : IJournalWriter
{
	private readonly StreamWriter _writer;
	private bool _disposed;
	public string Path { get; }

	public FileJournalWriter(string directory, string runId, string schemaVersion, string configHash, string? dataVersion = null)
	{
		Directory.CreateDirectory(directory);
		Path = System.IO.Path.Combine(directory, runId, "events.csv");
		var dir = System.IO.Path.GetDirectoryName(Path)!;
		Directory.CreateDirectory(dir);
		var exists = File.Exists(Path);
		_writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
		if (!exists)
		{
			var meta = $"schema_version={schemaVersion},config_hash={configHash}";
			if (!string.IsNullOrWhiteSpace(dataVersion)) meta += $",data_version={dataVersion}";
			_writer.WriteLine(meta);
			_writer.WriteLine("sequence,utc_ts,event_type,payload_json");
			_writer.Flush();
		}
	}

	public async Task AppendAsync(JournalEvent evt, CancellationToken ct = default)
	{
		if (_disposed) throw new ObjectDisposedException(nameof(FileJournalWriter));
		await _writer.WriteLineAsync(evt.ToCsvLine());
		await _writer.FlushAsync();
		// NOTE: fsync optional; can add conditional platform-specific flush here later.
	}

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
