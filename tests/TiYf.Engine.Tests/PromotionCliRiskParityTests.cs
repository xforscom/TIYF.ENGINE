using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

[Collection("E2E-Serial")] // serialize heavy end-to-end promotion runs
public class PromotionCliRiskParityTests
{
    private record CliResult(int ExitCode, string Stdout, string Stderr);

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i=0;i<12;i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src")) && File.Exists(Path.Combine(dir, "TiYf.Engine.sln"))) return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(dir); if (parent==null) break; dir = parent.FullName;
        }
        throw new InvalidOperationException("Cannot resolve repo root");
    }

    private static CliResult RunPromote(string baselineCfg, string candidateCfg)
    {
        string root = RepoRoot();
        string toolsDll = Path.Combine(root, "src","TiYf.Engine.Tools","bin","Debug","net8.0","TiYf.Engine.Tools.dll");
        if (!File.Exists(toolsDll))
        {
            var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet","build -c Debug --nologo") { WorkingDirectory = root });
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
        if (!proc.WaitForExit(180_000)) { try { proc.Kill(entireProcessTree:true); } catch {}; Assert.Fail($"Promotion timeout STDOUT\n{stdout}\nSTDERR\n{stderr}"); }
        return new CliResult(proc.ExitCode, stdout, stderr);
    }

    private static JsonElement ExtractResult(string stdout)
    {
        var line = stdout.Split('\n').FirstOrDefault(l=>l.Contains("PROMOTION_RESULT_V1", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(line), "PROMOTION_RESULT_V1 JSON line not found");
        using var doc = JsonDocument.Parse(line!);
        return doc.RootElement.Clone();
    }

    private static string WriteConfig(string srcCfg, string riskMode, bool injectExposureBreach=false)
    {
        var json = File.ReadAllText(srcCfg);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        // Ensure featureFlags exists
        if (!node.TryGetPropertyValue("featureFlags", out var ff) || ff is not System.Text.Json.Nodes.JsonObject)
        {
            node["featureFlags"] = new System.Text.Json.Nodes.JsonObject();
        }
        var ffObj = node["featureFlags"]!.AsObject();
        ffObj["risk"] = riskMode; // off|shadow|active
        ffObj["sentiment"] = ffObj.TryGetPropertyValue("sentiment", out var sVal) ? sVal : "disabled"; // preserve existing
        // Add riskConfig to allow deterministic evaluation (wide limits so no alerts unless we inject)
        var riskCfg = new System.Text.Json.Nodes.JsonObject
        {
            ["maxNetExposureBySymbol"] = new System.Text.Json.Nodes.JsonObject { ["EURUSD"] = 10_000_000 },
            ["maxRunDrawdownCCY"] = 9999999,
            ["blockOnBreach"] = true,
            ["emitEvaluations"] = true
        };
        if (injectExposureBreach)
        {
            // Force immediate exposure breach by setting per-symbol cap to 0
            riskCfg["maxNetExposureBySymbol"] = new System.Text.Json.Nodes.JsonObject { ["EURUSD"] = 0 };
            // Force strategy to place an order at the first minute so projection sees exposure before first eval
            if (node.TryGetPropertyValue("strategy", out var stratV2) && stratV2 is System.Text.Json.Nodes.JsonObject stratObj2 && stratObj2.TryGetPropertyValue("params", out var paramsV2) && paramsV2 is System.Text.Json.Nodes.JsonObject paramsObj2)
            {
                paramsObj2["proposalOffsetsMinutes"] = new System.Text.Json.Nodes.JsonArray { 0 };
            }
        }
        node["riskConfig"] = riskCfg;
        // Ensure strategy sizing explicit (to avoid fixture changes reducing exposure)
        if (node.TryGetPropertyValue("strategy", out var stratVal) && stratVal is System.Text.Json.Nodes.JsonObject stratObj && stratObj.TryGetPropertyValue("params", out var pVal) && pVal is System.Text.Json.Nodes.JsonObject pObj)
        {
            pObj["sizeUnitsFx"] = 1000; // deterministic size for exposure projection
        }
        var tmp = Path.Combine(Path.GetTempPath(), $"promo_risk_{riskMode}_{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, node.ToJsonString());
        return tmp;
    }

    [Fact]
    public void Promotion_Accepts_Benign_ShadowToActive_NoAlerts()
    {
        var root = RepoRoot();
        var baseSrc = Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.json");
        var baseline = WriteConfig(baseSrc, "shadow", injectExposureBreach:false);
        var candidate = WriteConfig(baseSrc, "active", injectExposureBreach:false);
        var res = RunPromote(baseline, candidate);
        var result = ExtractResult(res.Stdout);
        Assert.Equal(0, res.ExitCode);
        Assert.True(result.GetProperty("accepted").GetBoolean());
        var risk = result.GetProperty("risk");
        Assert.Equal("shadow", risk.GetProperty("baseline_mode").GetString());
        Assert.Equal("active", risk.GetProperty("candidate_mode").GetString());
        Assert.True(risk.GetProperty("parity").GetBoolean());
        Assert.Equal("ok", risk.GetProperty("reason").GetString());
        // Candidate alerts should be zero
        Assert.Equal(0, risk.GetProperty("candidate").GetProperty("alerts").GetInt32());
    }

    [Fact]
    public void Promotion_Rejects_Mode_Downgrade_ActiveToShadow()
    {
        var root = RepoRoot();
        var baseSrc = Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.json");
        var baseline = WriteConfig(baseSrc, "active", injectExposureBreach:false);
        var candidate = WriteConfig(baseSrc, "shadow", injectExposureBreach:false);
        var res = RunPromote(baseline, candidate);
        var result = ExtractResult(res.Stdout);
        Assert.Equal(2, res.ExitCode);
        Assert.False(result.GetProperty("accepted").GetBoolean());
    Assert.Equal("risk_mismatch", result.GetProperty("reason").GetString());
    }

    [Fact]
    public void Promotion_Rejects_ShadowToActive_WithUnexpectedAlerts()
    {
        var root = RepoRoot();
        var baseSrc = Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.json");
        var baseline = WriteConfig(baseSrc, "shadow", injectExposureBreach:false);
        var candidate = WriteConfig(baseSrc, "active", injectExposureBreach:true); // inject breach
        var res = RunPromote(baseline, candidate);
        var result = ExtractResult(res.Stdout);
        if (res.ExitCode != 2)
        {
            // Attempt to locate candidate run dir (hash of candidate config hash present in stdout? Not exposed yet)
            // Best effort: print stdout for manual inspection
            Console.WriteLine("PROMOTION_STDOUT:\n"+res.Stdout);
            Console.WriteLine("PROMOTION_STDERR:\n"+res.Stderr);
        }
        Assert.Equal(2, res.ExitCode);
        var risk = result.GetProperty("risk");
        Assert.False(result.GetProperty("accepted").GetBoolean());
    Assert.Equal("risk_mismatch", result.GetProperty("reason").GetString());
    // diff_hint may be 'unexpected_active_alerts' or 'exception' depending on alert parsing; only require risk mismatch.
    }
}
