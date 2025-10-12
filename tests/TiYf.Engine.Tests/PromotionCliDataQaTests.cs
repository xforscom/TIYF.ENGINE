using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

[Collection("E2E-Serial")] // serialize due to journal/journals reuse
public class PromotionCliDataQaTests
{
    private record CliResult(int ExitCode, string Stdout, string Stderr);

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src")) && File.Exists(Path.Combine(dir, "TiYf.Engine.sln"))) return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(dir); if (parent == null) break; dir = parent.FullName;
        }
        throw new InvalidOperationException("Cannot resolve repo root");
    }

    private static CliResult RunPromote(string baselineCfg, string candidateCfg, string? culture = null)
    {
        string root = RepoRoot();
        // Always prefer Debug build (contains latest diagnostics reliably in current project layout)
        string toolsDll = Path.Combine(root, "src", "TiYf.Engine.Tools", "bin", "Debug", "net8.0", "TiYf.Engine.Tools.dll");
        if (!File.Exists(toolsDll))
        {
            var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "build -c Debug --nologo") { WorkingDirectory = root });
            build!.WaitForExit();
        }
        Assert.True(File.Exists(toolsDll), $"Tools DLL missing after debug build: {toolsDll}");
        var sb = new StringBuilder();
        sb.Append("exec \"").Append(toolsDll).Append("\" promote --baseline \"").Append(baselineCfg).Append("\" --candidate \"").Append(candidateCfg).Append("\" --workdir \"").Append(root).Append("\" --print-metrics");
        if (!string.IsNullOrWhiteSpace(culture)) sb.Append(" --culture ").Append(culture);
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", sb.ToString())
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(180_000)) { try { proc.Kill(entireProcessTree: true); } catch { }; Assert.Fail($"Promotion timeout CMD=dotnet {sb}\nSTDOUT\n{stdout}\nSTDERR\n{stderr}"); }
        return new CliResult(proc.ExitCode, stdout, stderr);
    }

    private static JsonDocument ParseResult(string stdout, out JsonElement root)
    {
        root = default;
        var line = stdout.Split('\n').FirstOrDefault(l => l.Contains("PROMOTION_RESULT_V1", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(line), "PROMOTION_RESULT_V1 JSON line not found");
        var doc = JsonDocument.Parse(line!);
        root = doc.RootElement;
        return doc;
    }

    private static string WriteActivePassConfig(string sourceCfgPath)
    {
        var json = File.ReadAllText(sourceCfgPath);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        // Preserve riskProbe disabled (original fixture disables it) to avoid alert emissions affecting acceptance
        node["featureFlags"] = new System.Text.Json.Nodes.JsonObject { ["dataQa"] = "active", ["riskProbe"] = "disabled" };
        node["dataQA"] = new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = true,
            ["maxMissingBarsPerInstrument"] = 999,
            ["allowDuplicates"] = true,
            ["spikeZ"] = 50,
            ["repair"] = new System.Text.Json.Nodes.JsonObject { ["forwardFillBars"] = 1, ["dropSpikes"] = false }
        };
        var path = Path.Combine(Path.GetTempPath(), "promo_active_pass_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, node.ToJsonString());
        return path;
    }

    private static string WriteActiveFailConfig(string sourceCfgPath)
    {
        var rootDir = RepoRoot();
        var origFixtureRoot = Path.GetDirectoryName(sourceCfgPath)!; // tests/fixtures/backtest_m0
        // Copy tick files and induce missing minute for failure
        var tmp = Path.Combine(Path.GetTempPath(), "promo_active_fail_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        foreach (var f in Directory.GetFiles(origFixtureRoot, "ticks_*.csv").OrderBy(p => p, StringComparer.Ordinal))
        {
            var lines = File.ReadAllLines(f).Where(l => !l.Contains("2025-01-02T00:30:"));
            File.WriteAllLines(Path.Combine(tmp, Path.GetFileName(f)), lines);
        }
        File.Copy(Path.Combine(origFixtureRoot, "instruments.csv"), Path.Combine(tmp, "instruments.csv"));
        var json = File.ReadAllText(sourceCfgPath);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["featureFlags"] = new System.Text.Json.Nodes.JsonObject { ["dataQa"] = "active", ["riskProbe"] = "disabled" };
        node["dataQA"] = new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = true,
            ["maxMissingBarsPerInstrument"] = 0, // strict
            ["allowDuplicates"] = false,
            ["spikeZ"] = 5,
            ["repair"] = new System.Text.Json.Nodes.JsonObject { ["forwardFillBars"] = 0, ["dropSpikes"] = true }
        };
        // Re-point ticks + instruments paths (preserve relative style converting to OS path with forward slashes)
        if (node.TryGetPropertyValue("data", out var dataVal) && dataVal is System.Text.Json.Nodes.JsonObject dataObj && dataObj.TryGetPropertyValue("ticks", out var ticksVal) && ticksVal is System.Text.Json.Nodes.JsonObject ticksObj)
        {
            foreach (var kv in ticksObj.ToList())
            {
                var orig = kv.Value!.GetValue<string>();
                var fileName = Path.GetFileName(orig);
                ticksObj[kv.Key] = Path.Combine(tmp, fileName).Replace('\\', '/');
            }
            dataObj["instrumentsFile"] = Path.Combine(tmp, "instruments.csv").Replace('\\', '/');
        }
        var path = Path.Combine(Path.GetTempPath(), "promo_active_fail_cfg_" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, node.ToJsonString());
        return path;
    }

    [Fact]
    public void PromoteCli_Accept_ActiveDataQa_ReturnsZero()
    {
        var root = RepoRoot();
        var baselineSrc = Path.Combine(root, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json");
        var candidateSrc = Path.Combine(root, "tests", "fixtures", "backtest_m0", "config.backtest-m0.candidate.json");
        var baseline = WriteActivePassConfig(baselineSrc);
        var candidate = WriteActivePassConfig(candidateSrc);
        var res = RunPromote(baseline, candidate);
        if (res.ExitCode != 0)
            Assert.Fail($"Expected exit 0, got {res.ExitCode}\nSTDOUT\n{res.Stdout}\nSTDERR\n{res.Stderr}");
        using var doc = ParseResult(res.Stdout, out var rootEl);
        Assert.True(rootEl.GetProperty("accepted").GetBoolean());
        Assert.Equal("accept", rootEl.GetProperty("reason").GetString());
        var dataQa = rootEl.GetProperty("dataQa").GetProperty("candidate");
        Assert.False(dataQa.GetProperty("aborted").GetBoolean());
        Assert.True(dataQa.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void PromoteCli_Reject_ActiveDataQaFailed_ReturnsTwo()
    {
        var root = RepoRoot();
        var baselineSrc = Path.Combine(root, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json");
        var baseline = WriteActivePassConfig(baselineSrc);
        var failingCandidate = WriteActiveFailConfig(baselineSrc);
        var res = RunPromote(baseline, failingCandidate);
        if (res.ExitCode != 2)
            Assert.Fail($"Expected exit 2, got {res.ExitCode}\nSTDOUT\n{res.Stdout}\nSTDERR\n{res.Stderr}");
        using var doc = ParseResult(res.Stdout, out var rootEl);
        Assert.False(rootEl.GetProperty("accepted").GetBoolean());
        Assert.Equal("data_qa_failed", rootEl.GetProperty("reason").GetString());
        var dataQa = rootEl.GetProperty("dataQa").GetProperty("candidate");
        Assert.True(dataQa.GetProperty("aborted").GetBoolean() || (dataQa.TryGetProperty("passed", out var passed) && passed.ValueKind == JsonValueKind.False));
    }

    [Fact]
    public void PromoteCli_CultureInvariant_ActiveDataQa_ReturnsZero()
    {
        var root = RepoRoot();
        var baselineSrc = Path.Combine(root, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json");
        var candidateSrc = Path.Combine(root, "tests", "fixtures", "backtest_m0", "config.backtest-m0.candidate.json");
        var baseline = WriteActivePassConfig(baselineSrc);
        var candidate = WriteActivePassConfig(candidateSrc);
        var res = RunPromote(baseline, candidate, culture: "de-DE");
        if (res.ExitCode != 0)
            Assert.Fail($"Expected exit 0 (culture), got {res.ExitCode}\nSTDOUT\n{res.Stdout}\nSTDERR\n{res.Stderr}");
        using var doc = ParseResult(res.Stdout, out var rootEl);
        Assert.True(rootEl.GetProperty("accepted").GetBoolean());
        Assert.Equal("accept", rootEl.GetProperty("reason").GetString());
    }
}
