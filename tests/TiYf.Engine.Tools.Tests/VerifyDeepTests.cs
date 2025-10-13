using System;
using System.IO;
using System.Text.Json;
using TiYf.Engine.Core.Infrastructure;
using TiYf.Engine.Tools;
using Xunit;

namespace TiYf.Engine.Tools.Tests;

public class VerifyDeepTests
{
    private static string QuoteJson(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return "\"" + json.Replace("\"", "\"\"") + "\"";
    }
    private static (string eventsPath, string tradesPath) BuildHealthyJournal()
    {
        var strict = new VerifyStrictTests();
        return strict.BuildHealthyJournal();
    }

    [Fact]
    public void DeepVerify_Passes_ForHealthyJournal()
    {
        var (events, trades) = BuildHealthyJournal();
        var result = TiYf.Engine.Tools.DeepVerifyEngine.Run(new TiYf.Engine.Tools.DeepVerifyOptions(
            events,
            trades,
            Schema.Version,
            StrictOrdering: true,
            MaxErrors: 200,
            ReportDuplicates: true,
            SentimentMode: null
        ));
        Assert.True(result.ExitCode == 0, result.JsonSummary);

        using var doc = JsonDocument.Parse(result.JsonSummary);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("stats").GetProperty("hasBlockingAlerts").GetBoolean());
    }

    [Fact]
    public void DeepVerify_Fails_WhenBlockingAlertsPresent()
    {
        var (events, trades) = BuildHealthyJournal();
        var ts = "2025-01-01T00:00:00Z";
        var risk = QuoteJson(new { symbol = "EURUSD", ts, net_exposure = 200m, run_drawdown = 0m });
        File.AppendAllText(events, $"3,{ts},INFO_RISK_EVAL_V1,{risk}{Environment.NewLine}");
        var alert = QuoteJson(new { symbol = "EURUSD", limit = 100m, value = 200m, reason = "cap" });
        File.AppendAllText(events, $"4,{ts},ALERT_BLOCK_NET_EXPOSURE,{alert}{Environment.NewLine}");

        var result = TiYf.Engine.Tools.DeepVerifyEngine.Run(new TiYf.Engine.Tools.DeepVerifyOptions(
            events,
            trades,
            Schema.Version,
            StrictOrdering: true,
            MaxErrors: 200,
            ReportDuplicates: true,
            SentimentMode: null
        ));
        Assert.True(result.ExitCode == 2, result.JsonSummary);

        using var doc = JsonDocument.Parse(result.JsonSummary);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("stats").GetProperty("hasBlockingAlerts").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("stats").GetProperty("alertTypes").GetProperty("ALERT_BLOCK_NET_EXPOSURE").GetInt32());
    }
}
