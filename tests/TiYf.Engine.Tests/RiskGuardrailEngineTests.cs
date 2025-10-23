using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace TiYf.Engine.Tests;

public class RiskGuardrailEngineTests
{
    private string SolutionRoot => FindSolutionRoot();
    private string SimDll => Path.Combine(SolutionRoot, "src", "TiYf.Engine.Sim", "bin", "Release", "net8.0", "TiYf.Engine.Sim.dll");

    [Fact]
    public void Risk_Shadow_ParityWithOff()
    {
        var baseConfig = Path.Combine(SolutionRoot, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json");
        Assert.True(File.Exists(baseConfig));
        string cfgOff = TempConfigWithRisk(baseConfig, "off", null);
        string cfgShadow = TempConfigWithRisk(baseConfig, "shadow", null);
        var offRun = RunSim(cfgOff);
        var shadowRun = RunSim(cfgShadow);
        var offEvents = File.ReadAllLines(offRun.events);
        var shadowEvents = File.ReadAllLines(shadowRun.events);
        // Trades parity (normalize by skipping meta line)
        var offTrades = NormalizeTrades(File.ReadAllLines(offRun.trades));
        var shadowTrades = NormalizeTrades(File.ReadAllLines(shadowRun.trades));
        Assert.Equal(offTrades, shadowTrades);
        bool OffHasEval = offEvents.Any(l => l.Contains(",INFO_RISK_EVAL_V1,"));
        bool ShadowHasEval = shadowEvents.Any(l => l.Contains(",INFO_RISK_EVAL_V1,"));
        Assert.False(OffHasEval);
        Assert.True(ShadowHasEval);
        Assert.DoesNotContain(offEvents, l => l.Contains("ALERT_BLOCK_"));
        Assert.DoesNotContain(shadowEvents, l => l.Contains("ALERT_BLOCK_"));
    }

    [Fact]
    public void Risk_Active_NetExposure_BlocksTrade()
    {
        var baseConfig = Path.Combine(SolutionRoot, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json");
        string cfgActive = TempConfigWithRisk(baseConfig, "active", null); // helper sets EURUSD cap=0 guaranteeing breach
        var run = RunSim(cfgActive);
        var events = File.ReadAllLines(run.events);
        Assert.Contains(events, l => l.Contains("ALERT_BLOCK_NET_EXPOSURE"));
        var trades = File.ReadAllLines(run.trades);
        // Ensure at least one expected decision id is absent due to block (heuristic: original config has 6 rows -> expect fewer)
        Assert.True(trades.Length < 7, $"Expected trade suppression, got {trades.Length} lines");
    }

    [Fact]
    public void Risk_Active_Drawdown_BlocksTrade()
    {
        var baseConfig = Path.Combine(SolutionRoot, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json");
        // Configure tiny drawdown cap and force hook after 1 evaluation so second evaluation triggers breach
        string extra = "\"maxRunDrawdownCCY\":5,\"forceDrawdownAfterEvals\":{\"EURUSD\":1}"; // trigger on first EURUSD eval
        string cfgActive = TempConfigWithRisk(baseConfig, "active", extra);
        var run = RunSim(cfgActive);
        var events = File.ReadAllLines(run.events);
        Assert.Contains(events, l => l.Contains("ALERT_BLOCK_DRAWDOWN"));
        var trades = File.ReadAllLines(run.trades);
        // Expect at least one suppression due to drawdown block; same heuristic as exposure: total rows fewer than original 6
        Assert.True(trades.Length < 7, $"Expected drawdown-based trade suppression, got {trades.Length} lines");
    }

    [Fact]
    public void Risk_EventOrdering_EvalBeforeAlert_AlertBeforeTrade()
    {
        var baseConfig = Path.Combine(SolutionRoot, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json");
        // Force immediate exposure breach (EURUSD cap = 0) to get a deterministic ALERT_BLOCK_NET_EXPOSURE
        string cfgActive = TempConfigWithRisk(baseConfig, "active", null);
        var run = RunSim(cfgActive);
        var events = File.ReadAllLines(run.events);
        // Find first risk evaluation + alert pair
        int evalIdx = Array.FindIndex(events, l => l.Contains(",INFO_RISK_EVAL_V1,"));
        int alertIdx = Array.FindIndex(events, l => l.Contains(",ALERT_BLOCK_NET_EXPOSURE"));
        Assert.True(evalIdx > 0 && alertIdx > 0, "Expected evaluation and exposure alert present");
        Assert.True(evalIdx < alertIdx, $"INFO_RISK_EVAL_V1 must precede ALERT_BLOCK_NET_EXPOSURE (evalIdx={evalIdx}, alertIdx={alertIdx})");
        // Ensure no trade placed after alert for same minute (blocked) by checking trades count heuristic (already suppressed in prior test)
        var trades = File.ReadAllLines(run.trades);
        Assert.True(trades.Length < 7, "Trade suppression expected after alert");
    }

    private (string events, string trades) RunSim(string cfg)
    {
        Assert.True(File.Exists(SimDll), "Sim DLL missing. Build Release first.");
        var psi = new ProcessStartInfo("dotnet", $"exec \"{SimDll}\" --config \"{cfg}\" --quiet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = SolutionRoot };
        var p = Process.Start(psi)!; p.WaitForExit(60000);
        if (!p.HasExited) { try { p.Kill(); } catch { } throw new Exception("Sim timeout"); }
        Assert.Equal(0, p.ExitCode);
        var stdout = p.StandardOutput.ReadToEnd();
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string ResolvePath(string key, string fallbackRelative)
        {
            var match = lines.FirstOrDefault(l => l.StartsWith(key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                var rel = match.Substring(key.Length).Trim();
                var normalized = rel.Replace('/', Path.DirectorySeparatorChar);
                try
                {
                    return Path.GetFullPath(normalized, SolutionRoot);
                }
                catch (Exception)
                {
                    // fall back to legacy layout below
                }
            }
            var legacy = Path.Combine(SolutionRoot, fallbackRelative);
            return legacy;
        }

        var runIdLine = lines.FirstOrDefault(l => l.StartsWith("RUN_ID=", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(runIdLine));
        var runId = runIdLine!.Substring("RUN_ID=".Length).Trim();

        var eventsPath = ResolvePath("JOURNAL_DIR_EVENTS=", Path.Combine("journals", "M0", runId, "events.csv"));
        var tradesPath = ResolvePath("JOURNAL_DIR_TRADES=", Path.Combine("journals", "M0", runId, "trades.csv"));

        Assert.True(File.Exists(eventsPath), $"Events journal not found: {eventsPath}\nSTDOUT:{stdout}\nSTDERR:{p.StandardError.ReadToEnd()}");
        Assert.True(File.Exists(tradesPath), $"Trades journal not found: {tradesPath}\nSTDOUT:{stdout}\nSTDERR:{p.StandardError.ReadToEnd()}");
        return (eventsPath, tradesPath);
    }

    private string TempConfigWithRisk(string baseConfig, string riskMode, string? extraRiskFields)
    {
        var raw = File.ReadAllText(baseConfig);
        var root = System.Text.Json.Nodes.JsonNode.Parse(raw)!.AsObject();
        var flags = root["featureFlags"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        flags["risk"] = riskMode;
        root["featureFlags"] = flags;
        var rc = root["riskConfig"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        rc["emitEvaluations"] = true;
        rc["blockOnBreach"] = true;
        if (!rc.ContainsKey("maxRunDrawdownCCY")) rc["maxRunDrawdownCCY"] = 999999.0; // large so drawdown never trips unless overridden by extra
        if (riskMode == "active")
        {
            var caps = rc["maxNetExposureBySymbol"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
            if (caps["EURUSD"] == null) caps["EURUSD"] = 0; // guarantee breach
            rc["maxNetExposureBySymbol"] = caps;
        }
        if (!string.IsNullOrEmpty(extraRiskFields))
        {
            var extraNode = System.Text.Json.Nodes.JsonNode.Parse("{" + extraRiskFields + "}")!.AsObject();
            foreach (var kv in extraNode)
            {
                if (kv.Value is null) { rc[kv.Key] = null; continue; }
                var cloned = System.Text.Json.Nodes.JsonNode.Parse(kv.Value.ToJsonString());
                rc[kv.Key] = cloned;
            }
        }
        root["riskConfig"] = rc;
        var outPath = Path.Combine(Path.GetTempPath(), $"riskcfg_{Guid.NewGuid():N}.json");
        File.WriteAllText(outPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
        var confirm = File.ReadAllText(outPath);
        Assert.Contains("\"risk\":\"" + riskMode + "\"", confirm);
        Assert.Contains("\"emitEvaluations\":true", confirm);
        return outPath;
    }

    private static string[] NormalizeTrades(string[] raw)
    {
        if (raw.Length == 0) return raw;
        var header = raw[0].Split(',');
        int cfgIdx = Array.FindIndex(header, h => h.Equals("config_hash", StringComparison.OrdinalIgnoreCase));
        string Rebuild(string line)
        {
            var parts = line.Split(',');
            if (cfgIdx < 0 || parts.Length != header.Length) return line.TrimEnd();
            var kept = parts.Where((p, i) => i != cfgIdx);
            return string.Join(',', kept).TrimEnd();
        }
        return raw.Select(Rebuild).ToArray();
    }

    [Fact]
    public void TempConfigWithRisk_WritesExpectedFlags()
    {
        var tmp = TempConfigWithRisk(Path.Combine(SolutionRoot, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json"), "shadow", null);
        var text = File.ReadAllText(tmp);
        Assert.Contains("\"featureFlags\":{", text);
        Assert.Contains("\"risk\":\"shadow\"", text);
        Assert.Contains("\"emitEvaluations\":true", text);
    }

    [Fact]
    public void RiskConfigParsing_RespectsFeatureFlag()
    {
        var tmp = TempConfigWithRisk(Path.Combine(SolutionRoot, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json"), "shadow", null);
        var raw = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(tmp));
        var mode = TiYf.Engine.Sim.RiskParsing.ParseRiskMode(raw.RootElement);
        Assert.Equal(TiYf.Engine.Sim.RiskMode.Shadow, mode);
    }

    private static string FindSolutionRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !File.Exists(Path.Combine(dir, "TiYf.Engine.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir ?? Directory.GetCurrentDirectory();
    }
}
