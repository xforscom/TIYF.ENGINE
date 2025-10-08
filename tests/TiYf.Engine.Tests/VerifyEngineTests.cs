using Xunit;
using System.IO;
using System.Text;
using System.Text.Json;
using TiYf.Engine.Tools;

namespace TiYf.Engine.Tests;

public class VerifyEngineTests
{
    private static string Serialize(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions{WriteIndented=false});

    private string MakeBarJournal(params object[] barPayloads)
    {
        var path = Path.Combine(Path.GetTempPath(), "verify-"+System.Guid.NewGuid().ToString("N")+".csv");
        var sb = new StringBuilder();
    sb.AppendLine("schema_version=1.2.0,config_hash=HASH");
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
        var result = VerifyEngine.Run(path, new VerifyOptions(50,false,false));
        Assert.Equal(0, result.ExitCode);
    }
}
