using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;
using TiYf.Engine.Tools;

namespace TiYf.Engine.Tools.Tests;

public class VerifyStrictTests
{
    private static string CsvQuote(string json) => "\"" + json.Replace("\"", "\"\"") + "\"";
    private string TempFile(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(),$"strict_{Guid.NewGuid():N}_{name}");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private (string eventsPath,string tradesPath) BuildHealthyJournal()
    {
        var eventsSb = new StringBuilder();
        eventsSb.AppendLine("schema_version=1.2.0,config_hash=ABC123");
        eventsSb.AppendLine("sequence,utc_ts,event_type,payload_json");
        // BAR (seq 1)
        var ts = new DateTime(2025,1,1,0,0,0,DateTimeKind.Utc).ToString("O");
        var barPayload = JsonSerializer.Serialize(new { InstrumentId = new { Value="EURUSD" }, IntervalSeconds=60, StartUtc=ts, EndUtc=ts, Open=1.1000m, High=1.1005m, Low=1.0995m, Close=1.1002m, Volume=10 });
        eventsSb.AppendLine($"1,{ts},BAR_V1,{CsvQuote(barPayload)}");
        // SENTIMENT Z (seq 2)
        var zPayload = JsonSerializer.Serialize(new { symbol="EURUSD", z=0.0m, window=5, sigma=0.1m });
        eventsSb.AppendLine($"2,{ts},INFO_SENTIMENT_Z_V1,{CsvQuote(zPayload)}");
        var eventsPath = TempFile("events.csv", eventsSb.ToString());
        var tradesSb = new StringBuilder();
        tradesSb.AppendLine("utc_ts_open,utc_ts_close,symbol,direction,entry_price,exit_price,volume_units,pnl_ccy,pnl_r,decision_id,schema_version,config_hash,data_version");
        tradesSb.AppendLine($"{ts},{ts},EURUSD,BUY,1.1000,1.1002,100,0.20,0,DEC1,1.2.0,ABC123,");
        var tradesPath = TempFile("trades.csv", tradesSb.ToString());
        return (eventsPath,tradesPath);
    }

    [Fact]
    public void Verify_Strict_Accepts_HealthyJournal()
    {
        var (events,trades) = BuildHealthyJournal();
        var report = StrictJournalVerifier.Verify(new StrictVerifyRequest(events,trades,"1.2.0", strict:true));
        Assert.Equal(0, report.ExitCode);
        Assert.Empty(report.Violations);
    }

    [Fact]
    public void Verify_Strict_Rejects_UnknownEvent()
    {
        var (events,trades) = BuildHealthyJournal();
        // append unknown event
    File.AppendAllText(events, $"3,2025-01-01T00:00:00Z,FOO_BAR_V99,\"{{}}\"{Environment.NewLine}");
        var report = StrictJournalVerifier.Verify(new StrictVerifyRequest(events,trades,"1.2.0", strict:true));
        Assert.Equal(2, report.ExitCode);
        Assert.Contains(report.Violations, v=>v.Kind=="unknown_event");
    }

    [Fact]
    public void Verify_Strict_Rejects_MissingField()
    {
        var (events,trades) = BuildHealthyJournal();
        // Add APPLIED missing symbol
    var applied = CsvQuote(JsonSerializer.Serialize(new { scaled_from=10, scaled_to=5, reason="guard" }));
    File.AppendAllText(events, $"3,2025-01-01T00:00:00Z,INFO_SENTIMENT_APPLIED_V1,{applied}{Environment.NewLine}");
        var report = StrictJournalVerifier.Verify(new StrictVerifyRequest(events,trades,"1.2.0", strict:true));
        Assert.Equal(2, report.ExitCode);
        Assert.Contains(report.Violations, v=>v.Kind=="missing_field");
    }

    [Fact]
    public void Verify_Strict_Rejects_OrderViolation()
    {
        var (events,trades) = BuildHealthyJournal();
        // Insert APPLIED before Z order (swap sequence intentionally broken: seq 2 becomes applied, seq3 Z)
    var content = File.ReadAllText(events).Replace("INFO_SENTIMENT_Z_V1","INFO_SENTIMENT_APPLIED_V1");
    File.WriteAllText(events, content);
    var z = CsvQuote(JsonSerializer.Serialize(new { symbol="EURUSD", z=0m, window=5, sigma=0.1m }));
    File.AppendAllText(events, $"3,2025-01-01T00:00:00Z,INFO_SENTIMENT_Z_V1,{z}{Environment.NewLine}");
        var report = StrictJournalVerifier.Verify(new StrictVerifyRequest(events,trades,"1.2.0", strict:true));
        Assert.Equal(2, report.ExitCode);
        Assert.Contains(report.Violations, v=>v.Kind=="order_violation");
    }

    [Fact]
    public void Verify_Strict_Rejects_NumericFormat()
    {
        var (events,trades) = BuildHealthyJournal();
        // introduce scientific notation in pnl_ccy
        var lines = File.ReadAllLines(trades);
        lines[1] = lines[1].Replace(",0.20,",",2.0E-1,");
        File.WriteAllLines(trades, lines);
        var report = StrictJournalVerifier.Verify(new StrictVerifyRequest(events,trades,"1.2.0", strict:true));
        Assert.Equal(2, report.ExitCode);
        Assert.Contains(report.Violations, v=>v.Kind=="numeric_format");
    }

    [Fact]
    public void Verify_Strict_Rejects_ShadowApplied()
    {
        var (events,trades) = BuildHealthyJournal();
        // Add APPLIED event with sentinel indicating shadow mode (simulate config mode=shadow by passing mode)
    var appliedOk = CsvQuote(JsonSerializer.Serialize(new { symbol="EURUSD", scaled_from=10, scaled_to=5, reason="guard" }));
    File.AppendAllText(events, $"3,2025-01-01T00:00:00Z,INFO_SENTIMENT_APPLIED_V1,{appliedOk}{Environment.NewLine}");
        var report = StrictJournalVerifier.Verify(new StrictVerifyRequest(events,trades,"1.2.0", strict:true, sentimentMode:"shadow"));
        Assert.Equal(2, report.ExitCode);
        Assert.Contains(report.Violations, v=>v.Kind=="mode_violation");
    }
}
