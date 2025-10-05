using TiYf.Engine.Sim;
using TiYf.Engine.Core;
using TiYf.Engine.Sidecar;
using TiYf.Engine.Tools;

public class RiskProbeEndToEndTests
{
    [Fact]
    public async Task Verify_RiskProbe_EndToEnd_Passes()
    {
        var root = Path.Combine("journals","RISKPROBE-TEST-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"inst.csv"), "id,symbol,decimals\nI1,FOO,2\n");
        var tickFile = Path.Combine(root, "ticks.csv");
        var start = DateTime.UtcNow.AddMinutes(-2);
        var aligned = new DateTime(start.Year,start.Month,start.Day,start.Hour,start.Minute,0,DateTimeKind.Utc);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("utc_ts,price,volume");
        sb.AppendLine($"{aligned:O},100,1");
        sb.AppendLine($"{aligned.AddSeconds(10):O},101,1");
        sb.AppendLine($"{aligned.AddSeconds(30):O},99.5,1");
        sb.AppendLine($"{aligned.AddSeconds(50):O},100.5,1");
        sb.AppendLine($"{aligned.AddMinutes(1):O},101.2,1");
        File.WriteAllText(tickFile, sb.ToString());
        var journalRoot = Path.Combine(root, "out");
        var cfg = new EngineConfig("1.1.0","RUN","inst.csv","ticks.csv",journalRoot,"BAR_V1","sequence",new[]{"I1"}, new[]{"1m"});
        var ticks = new CsvTickSource(tickFile, new InstrumentId("I1"));
        var clock = new DeterministicSequenceClock(ticks.Select(t=>t.UtcTimestamp));
        var tracker = new InMemoryBarKeyTracker();
        var interval = new BarInterval(TimeSpan.FromMinutes(1));
        var builders = new Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder>{ {(new InstrumentId("I1"), interval), new IntervalBarBuilder(interval)} };
        var journalWriter = new FileJournalWriter(journalRoot, cfg.RunId, cfg.SchemaVersion, ConfigHash.Compute(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(cfg)));
        var loop = new EngineLoop(clock, builders, tracker, journalWriter, ticks, cfg.BarOutputEventType, riskFormulas: new RiskFormulas(), basketAggregator: new BasketRiskAggregator(), configHash: "HASH", schemaVersion: cfg.SchemaVersion);
        await loop.RunAsync();
        await journalWriter.DisposeAsync();
        var journalFile = Path.Combine(journalRoot, cfg.RunId, "events.csv");
        var result = VerifyEngine.Run(journalFile, new VerifyOptions(50,false,false));
        Assert.Equal(0, result.ExitCode);
    }
}
