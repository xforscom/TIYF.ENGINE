using Xunit;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Globalization;

public class VerifyEngineTests
{
    private static string Serialize(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions{WriteIndented=false});

    private string MakeBarJournal(params object[] barPayloads)
    {
        var path = Path.Combine(Path.GetTempPath(), "verify-"+System.Guid.NewGuid().ToString("N")+".csv");
        var sb = new StringBuilder();
        sb.AppendLine("schema_version=1.1.0,config_hash=HASH");
        sb.AppendLine("sequence,utc_ts,event_type,payload_json");
        long seq=1;
        foreach (var payload in barPayloads)
        {
            var json = Serialize(payload).Replace("\"","\"\"");
            var ts = ((dynamic)payload).StartUtc as string ?? $"2025-10-05T10:00:0{seq}Z"; // fallback
            sb.AppendLine($"{seq},{ts},BAR_V1,\"{json}\"");
            seq++;
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    [Fact]
    public void Verify_ValidJournal_ReturnsZero()
    {
        var bar = new {
            InstrumentId = new { Value = "EURUSD" },
            IntervalSeconds = 60,
            StartUtc = "2025-10-05T10:00:00Z",
            EndUtc = "2025-10-05T10:01:00Z",
            Open = 1.1m,
            High = 1.2m,
            Low = 1.0m,
            Close = 1.15m,
            Volume = 1000m
        };
        var path = MakeBarJournal(bar);
    var result = global::VerifyEngine.Run(path, new global::VerifyOptions(50,false,false));
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Verify_MissingSchemaOrHash_ReturnsOne()
    {
        var path = Path.Combine(Path.GetTempPath(), "verify-"+System.Guid.NewGuid().ToString("N")+"-bad.csv");
        File.WriteAllText(path, "schema_version=1.1.0\nsequence,utc_ts,event_type,payload_json\n1,2025-10-05T10:00:00Z,BAR_V1,{}\n");
    var result = global::VerifyEngine.Run(path, new global::VerifyOptions(50,false,false));
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void Verify_NonUtcTimestamp_ReturnsOne()
    {
        var path = Path.Combine(Path.GetTempPath(), "verify-"+System.Guid.NewGuid().ToString("N")+"-badts.csv");
        File.WriteAllText(path, "schema_version=1.1.0,config_hash=HASH\nsequence,utc_ts,event_type,payload_json\n1,2025-10-05T10:00:00+02:00,BAR_V1,{}\n");
        var result = VerifyEngine.Run(path, new VerifyOptions(50,false,false));
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void Verify_BarCompositeKeyDuplicate_ReturnsOne_AndListsKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), "verify-"+System.Guid.NewGuid().ToString("N")+"-dup.csv");
        var barPayload = Serialize(new {
            InstrumentId = new { Value = "EURUSD" },
            IntervalSeconds = 60,
            StartUtc = "2025-10-05T10:00:00Z",
            EndUtc = "2025-10-05T10:01:00Z",
            Open = 1.1m,
            High = 1.2m,
            Low = 1.0m,
            Close = 1.15m,
            Volume = 1000m
        }).Replace("\"","\"\"");
        var sb = new StringBuilder();
        sb.AppendLine("schema_version=1.1.0,config_hash=H");
        sb.AppendLine("sequence,utc_ts,event_type,payload_json");
        sb.AppendLine($"1,2025-10-05T10:00:00Z,BAR_V1,\"{barPayload}\"");
        sb.AppendLine($"2,2025-10-05T10:00:00Z,BAR_V1,\"{barPayload}\"");
        File.WriteAllText(path, sb.ToString());
    var result = global::VerifyEngine.Run(path, new global::VerifyOptions(50,false,true));
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("duplicate composite key", result.HumanSummary);
    }

    [Fact]
    public void Verify_RiskProbe_MinColumns_Enforced()
    {
        var path = Path.Combine(Path.GetTempPath(), "verify-"+System.Guid.NewGuid().ToString("N")+"-risk.csv");
        var riskPayload = Serialize(new {
            InstrumentId = new { Value = "EURUSD" },
            ProjectedLeverage = 5.0,
            ProjectedMarginUsagePct = 12.3,
            BasketRiskPct = 3.4
        }).Replace("\"","\"\"");
        var sb = new StringBuilder();
        sb.AppendLine("schema_version=1.1.0,config_hash=H");
        sb.AppendLine("sequence,utc_ts,event_type,payload_json");
        sb.AppendLine($"1,2025-10-05T10:00:00Z,RISK_PROBE_V1,\"{riskPayload}\"");
        File.WriteAllText(path, sb.ToString());
    var result = global::VerifyEngine.Run(path, new global::VerifyOptions(50,false,false));
        Assert.Equal(0, result.ExitCode);
    }
}
