using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

internal static class JournalTestHelper
{
    public static string CreateBarJournal(IEnumerable<(string instrument, string startUtc, double open, double high, double low, double close, double vol, int intervalSeconds)> bars, string? filePath = null)
    {
        filePath ??= Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-A.csv");
        var sb = new StringBuilder();
        sb.AppendLine("schema_version=1.1.0,config_hash=TESTHASH");
        sb.AppendLine("sequence,utc_ts,event_type,payload_json");
        long seq = 1;
        foreach (var b in bars)
        {
            var endUtc = DateTime.Parse(b.startUtc).AddSeconds(b.intervalSeconds).ToUniversalTime();
            var payload = new
            {
                InstrumentId = new { Value = b.instrument },
                IntervalSeconds = b.intervalSeconds,
                StartUtc = b.startUtc,
                EndUtc = endUtc.ToString("O"),
                Open = b.open,
                High = b.high,
                Low = b.low,
                Close = b.close,
                Volume = b.vol
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            json = json.Replace("\"", "\"\""); // CSV escape quotes
            sb.AppendLine($"{seq},{b.startUtc},BAR_V1,\"{json}\"");
            seq++;
        }
        File.WriteAllText(filePath, sb.ToString());
        return filePath;
    }

    public static string DuplicateBarJournal(string instrument, string startUtc, int intervalSeconds)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-DUP.csv");
        var endUtc = DateTime.Parse(startUtc).AddSeconds(intervalSeconds).ToUniversalTime();
        var basePayload = new
        {
            InstrumentId = new { Value = instrument },
            IntervalSeconds = intervalSeconds,
            StartUtc = startUtc,
            EndUtc = endUtc.ToString("O"),
            Open = 100.0,
            High = 101.0,
            Low = 99.5,
            Close = 100.5,
            Volume = 5.5
        };
        var altPayload = new
        {
            InstrumentId = new { Value = instrument },
            IntervalSeconds = intervalSeconds,
            StartUtc = startUtc,
            EndUtc = endUtc.ToString("O"),
            Open = 100.0,
            High = 101.5,
            Low = 99.2,
            Close = 100.1,
            Volume = 6.0
        };
        string Esc(object o) { var j = System.Text.Json.JsonSerializer.Serialize(o); return j.Replace("\"", "\"\""); }
        var sb = new StringBuilder();
        sb.AppendLine("schema_version=1.1.0,config_hash=TESTHASH");
        sb.AppendLine("sequence,utc_ts,event_type,payload_json");
        sb.AppendLine($"1,{startUtc},BAR_V1,\"{Esc(basePayload)}\"");
        sb.AppendLine($"2,{startUtc},BAR_V1,\"{Esc(altPayload)}\""); // duplicate composite key
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
