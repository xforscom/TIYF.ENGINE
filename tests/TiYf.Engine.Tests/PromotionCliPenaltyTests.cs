using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

[Collection("E2E-Serial")] // serialize promotion runs
public class PromotionCliPenaltyTests
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

    private static CliResult RunPromote(string baselineCfg, string candidateCfg)
    {
        string root = RepoRoot();
        string toolsDll = Path.Combine(root, "src", "TiYf.Engine.Tools", "bin", "Debug", "net8.0", "TiYf.Engine.Tools.dll");
        if (!File.Exists(toolsDll))
        {
            var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "build -c Debug --nologo") { WorkingDirectory = root });
            build!.WaitForExit();
        }
        Assert.True(File.Exists(toolsDll), $"Tools DLL missing after debug build: {toolsDll}");
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"exec \"{toolsDll}\" promote --baseline \"{baselineCfg}\" --candidate \"{candidateCfg}\" --workdir \"{root}\" --print-metrics")
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
        if (!proc.WaitForExit(180_000)) { try { proc.Kill(entireProcessTree: true); } catch { }; Assert.Fail($"Promotion timeout STDOUT\n{stdout}\nSTDERR\n{stderr}"); }
        return new CliResult(proc.ExitCode, stdout, stderr);
    }

    private static JsonElement ExtractResult(string stdout)
    {
        var line = stdout.Split('\n').FirstOrDefault(l => l.Contains("PROMOTION_RESULT_V1", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(line), "PROMOTION_RESULT_V1 JSON line not found");
        using var doc = JsonDocument.Parse(line!);
        return doc.RootElement.Clone();
    }

    private static string WritePenaltyConfig(string srcCfg, string penaltyMode, bool forcePenalty, string sentimentMode = "off")
    {
        var json = File.ReadAllText(srcCfg);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        if (!node.TryGetPropertyValue("featureFlags", out var ff) || ff is not System.Text.Json.Nodes.JsonObject)
        {
            node["featureFlags"] = new System.Text.Json.Nodes.JsonObject();
        }
        var ffObj = node["featureFlags"]!.AsObject();
        ffObj["penalty"] = penaltyMode; // off|shadow|active
        ffObj["sentiment"] = sentimentMode; // off|shadow|active
        node["penaltyConfig"] = new System.Text.Json.Nodes.JsonObject { ["forcePenalty"] = forcePenalty };
        var tmp = Path.Combine(Path.GetTempPath(), $"promo_pen_{penaltyMode}_{sentimentMode}_{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, node.ToJsonString());
        return tmp;
    }

    [Fact]
    public void Promotion_Accepts_ShadowToActive_WithZeroPenalties()
    {
        var root = RepoRoot();
        var baseSrc = Path.Combine(root, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json");
        var baseline = WritePenaltyConfig(baseSrc, penaltyMode: "shadow", forcePenalty: false, sentimentMode: "off");
        var candidate = WritePenaltyConfig(baseSrc, penaltyMode: "active", forcePenalty: false, sentimentMode: "off");
        var res = RunPromote(baseline, candidate);
        var result = ExtractResult(res.Stdout);
        Assert.Equal(0, res.ExitCode);
        Assert.True(result.GetProperty("accepted").GetBoolean());
        // penalty block present
        var penalty = result.GetProperty("penalty");
        Assert.Equal("ok", penalty.GetProperty("reason").GetString());
    }

    [Fact]
    public void Promotion_Rejects_ActiveVsActive_OnPenaltyOrder()
    {
        var root = RepoRoot();
        var baseSrc = Path.Combine(root, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json");
        // Baseline: penalty active forced, sentiment off
        var baseline = WritePenaltyConfig(baseSrc, penaltyMode: "active", forcePenalty: true, sentimentMode: "off");
        // Candidate: penalty active forced, but sentiment shadow (inserts extra INFO_SENTIMENT_* before penalty; sequence differs)
        var candidate = WritePenaltyConfig(baseSrc, penaltyMode: "active", forcePenalty: true, sentimentMode: "shadow");
        var res = RunPromote(baseline, candidate);
        var result = ExtractResult(res.Stdout);
        Assert.Equal(2, res.ExitCode);
        Assert.False(result.GetProperty("accepted").GetBoolean());
        Assert.Equal("penalty_mismatch", result.GetProperty("reason").GetString());
    }
}
