using System.Text.RegularExpressions;
using TiYf.Engine.Core;
using TiYf.Engine.Sidecar;
using TiYf.Engine.Sim;

namespace TiYf.Engine.Tests;

public class BarV1EmissionTests
{
    [Fact]
    public async Task BarWriter_EmitsOnly_BAR_V1_WithExpectedColumns()
    {
        // Arrange: minimal instrument + ticks for exactly one 1m bar
        var root = Path.Combine("journals", "BARV1-TEST-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var instrFile = Path.Combine(root, "inst.csv");
        File.WriteAllText(instrFile, "id,symbol,decimals\nI1,FOO,2\n");
        var tickFile = Path.Combine(root, "ticks.csv");
        var start = DateTime.UtcNow.AddMinutes(-2);
        var aligned = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0, DateTimeKind.Utc);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("utc_ts,price,volume");
        sb.AppendLine($"{aligned:O},100,1");
        sb.AppendLine($"{aligned.AddSeconds(10):O},101,1");
        sb.AppendLine($"{aligned.AddSeconds(30):O},99.5,1");
        sb.AppendLine($"{aligned.AddSeconds(50):O},100.5,1");
        // Add a tick in the next minute to force bar closure and emission
        sb.AppendLine($"{aligned.AddMinutes(1):O},101.2,1");
        File.WriteAllText(tickFile, sb.ToString());
        var journalRoot = Path.Combine(root, "out");
        var cfg = new EngineConfig("1.1.0", "RUN", "barv1-test", "inst.csv", "ticks.csv", journalRoot, "BAR_V1", "sequence", Instruments: new[] { "I1" }, Intervals: new[] { "1m" });

        // Build runtime pieces manually (avoid needing external config file)
        var (config, configHash, _) = (cfg, ConfigHash.Compute(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(cfg)), (System.Text.Json.JsonDocument?)null);
        var instruments = File.ReadLines(instrFile).Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Split(',')).Select(p => new Instrument(new InstrumentId(p[0]), p[1], int.Parse(p[2]), 0.0001m)).ToList();
        var catalog = new InMemoryInstrumentCatalog(instruments);
        var ticks = new CsvTickSource(tickFile, new InstrumentId("I1"));
        var clock = new DeterministicSequenceClock(ticks.Select(t => t.UtcTimestamp));
        var journalWriter = new FileJournalWriter(journalRoot, cfg.RunId, cfg.SchemaVersion, configHash, "stub", "demo-stub", "account-stub");
        var tracker = new InMemoryBarKeyTracker();
        var interval = new BarInterval(TimeSpan.FromMinutes(1));
        var builders = new Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder> { { (new InstrumentId("I1"), interval), new IntervalBarBuilder(interval) } };
        var loop = new EngineLoop(clock, builders, tracker, journalWriter, ticks, cfg.BarOutputEventType, schemaVersion: cfg.SchemaVersion);
        await loop.RunAsync();
        await journalWriter.DisposeAsync();

        // Assert: read journal
        var journalFile = Path.Combine(journalRoot, "stub", cfg.RunId, "events.csv");
        Assert.True(File.Exists(journalFile), "Journal file not created");
        var lines = File.ReadAllLines(journalFile);
        Assert.True(lines.Length >= 3, "Expected header + at least one BAR_V1 row");
        // Header line (metadata) then header row sequence,utc_ts,event_type,payload_json
        Assert.StartsWith("schema_version=", lines[0]);
        Assert.Contains("adapter_id=stub", lines[0]);
        Assert.Contains("broker=demo-stub", lines[0]);
        Assert.Contains("account_id=account-stub", lines[0]);
        Assert.Equal("sequence,utc_ts,event_type,src_adapter,payload_json", lines[1]);
        var barRows = lines.Skip(2).Where(l => l.Contains(",BAR_V1,")).ToList();
        Assert.True(barRows.Count > 0, "No BAR_V1 rows present");
        // Ensure no legacy BAR row missing IntervalSeconds inside payload
        foreach (var r in barRows)
        {
            var parts = SplitCsv(r);
            Assert.Equal(5, parts.Length); // sequence, utc_ts, event_type, src_adapter, payload_json
            Assert.Equal("stub", parts[3]);
            var payloadField = parts[4];
            if (payloadField.StartsWith('"') && payloadField.EndsWith('"'))
            {
                payloadField = payloadField.Substring(1, payloadField.Length - 2).Replace("\"\"", "\"");
            }
            using var doc = System.Text.Json.JsonDocument.Parse(payloadField);
            var rootEl = doc.RootElement;
            Assert.True(rootEl.TryGetProperty("IntervalSeconds", out var intervalProp), "Legacy BAR format detected; only BAR_V1 is allowed.");
            Assert.Equal(60, intervalProp.GetDouble());
            Assert.True(rootEl.TryGetProperty("InstrumentId", out var instEl));
            Assert.True(rootEl.TryGetProperty("StartUtc", out _));
            Assert.True(rootEl.TryGetProperty("EndUtc", out _));
            Assert.True(rootEl.TryGetProperty("Open", out _));
            Assert.True(rootEl.TryGetProperty("High", out _));
            Assert.True(rootEl.TryGetProperty("Low", out _));
            Assert.True(rootEl.TryGetProperty("Close", out _));
            Assert.True(rootEl.TryGetProperty("Volume", out _));
        }
        // Metadata schema version should be 1.1.0
        Assert.Contains("schema_version=1.1.0", lines[0]);
    }

    private static string[] SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else if (c == '"') inQuotes = true;
                else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }
}
