using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

[Collection("E2E-Serial")] // serialize journals
public class PromotionCliSentimentTests
{
    private record CliResult(int ExitCode, string Stdout, string Stderr);

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory; // tests/.../bin/Release/net8.0
        for (int i=0;i<12;i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src")) && Directory.Exists(Path.Combine(dir, "tests"))) return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var p = Directory.GetParent(dir); if (p==null) break; dir = p.FullName;
        }
        throw new InvalidOperationException("Repo root not found");
    }

    private static CliResult RunPromote(string baselineCfg, string candidateCfg, string? culture = null)
    {
        string root = RepoRoot();
        string toolsDll = Path.Combine(root, "src","TiYf.Engine.Tools","bin","Release","net8.0","TiYf.Engine.Tools.dll");
        if (!File.Exists(toolsDll))
        {
            var build = Process.Start(new ProcessStartInfo("dotnet","build -c Release --nologo") { WorkingDirectory = root })!;
            build.WaitForExit();
        }
        else
        {
            // Rebuild to ensure Program.cs changes (promotion gating) are present; avoid stale Debug assembly being picked up indirectly
            var rebuild = Process.Start(new ProcessStartInfo("dotnet","build -c Release --nologo --no-incremental") { WorkingDirectory = root })!;
            rebuild.WaitForExit();
        }
        Assert.True(File.Exists(toolsDll), $"Tools DLL missing after Release build: {toolsDll}");
        var sb = new StringBuilder();
        sb.Append("exec \"").Append(toolsDll).Append("\" promote --baseline \"").Append(baselineCfg).Append("\" --candidate \"").Append(candidateCfg).Append("\" --workdir \"").Append(root).Append("\" --print-metrics");
        if (!string.IsNullOrWhiteSpace(culture)) sb.Append(" --culture ").Append(culture);
        var psi = new ProcessStartInfo("dotnet", sb.ToString())
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(180_000)) { try { proc.Kill(entireProcessTree:true);} catch {}; Assert.Fail($"promote timeout CMD=dotnet {sb}\nSTDOUT\n{stdout}\nSTDERR\n{stderr}"); }
        return new CliResult(proc.ExitCode, stdout, stderr);
    }

    private static JsonElement ExtractResult(string stdout)
    {
        var line = stdout.Split('\n').FirstOrDefault(l=>l.Contains("PROMOTION_RESULT_V1", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(line), "PROMOTION_RESULT_V1 JSON not found in stdout");
        using var doc = JsonDocument.Parse(line!);
        return doc.RootElement.Clone();
    }

    private static string WriteConfig(string sourceCfg, Action<System.Text.Json.Nodes.JsonObject> mutate)
    {
        var json = File.ReadAllText(sourceCfg);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        mutate(node);
        var path = Path.Combine(Path.GetTempPath(), "promo_sentiment_"+Guid.NewGuid().ToString("N")+".json");
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        return path;
    }

    // Helper to ensure a config that will not clamp (choose high volGuardSigma)
    private static void MakeNoClampActive(System.Text.Json.Nodes.JsonObject node)
    {
        node["featureFlags"] = new System.Text.Json.Nodes.JsonObject { ["sentiment"] = "active", ["riskProbe"] = "disabled" };
        node["sentimentConfig"] = new System.Text.Json.Nodes.JsonObject { ["window"] = 8, ["volGuardSigma"] = 99999 }; // huge threshold -> no clamp
    }

    // Helper to force clamp by using tiny volGuardSigma
    private static void MakeClampActive(System.Text.Json.Nodes.JsonObject node)
    {
        node["featureFlags"] = new System.Text.Json.Nodes.JsonObject { ["sentiment"] = "active", ["riskProbe"] = "disabled" };
        node["sentimentConfig"] = new System.Text.Json.Nodes.JsonObject { ["window"] = 5, ["volGuardSigma"] = 0.0000001 }; // extremely sensitive
    }

    private static void MakeShadow(System.Text.Json.Nodes.JsonObject node)
    {
        node["featureFlags"] = new System.Text.Json.Nodes.JsonObject { ["sentiment"] = "shadow", ["riskProbe"] = "disabled" };
        node["sentimentConfig"] = new System.Text.Json.Nodes.JsonObject { ["window"] = 5, ["volGuardSigma"] = 0.0000001 }; // shadow sees Z+CLAMP (if any)
    }

    private static void MakeOff(System.Text.Json.Nodes.JsonObject node)
    {
        node["featureFlags"] = new System.Text.Json.Nodes.JsonObject { ["sentiment"] = "off", ["riskProbe"] = "disabled" };
    }

    [Fact]
    public void Promote_Sentiment_Accept_ActiveVsActive()
    {
        string root = RepoRoot();
        var baseFixture = Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.candidate.json");
        var baseline = WriteConfig(baseFixture, MakeClampActive); // ensure clamps/APPLIED present
        var candidate = WriteConfig(baseFixture, MakeClampActive);
        var res = RunPromote(baseline, candidate);
        if (res.ExitCode != 0)
            Assert.Fail($"Expected accept exit 0 got {res.ExitCode}\nSTDOUT\n{res.Stdout}\nSTDERR\n{res.Stderr}");
        var rootEl = ExtractResult(res.Stdout);
        var sentiment = rootEl.GetProperty("sentiment");
        Assert.Equal("active", sentiment.GetProperty("baseline_mode").GetString());
        Assert.Equal("active", sentiment.GetProperty("candidate_mode").GetString());
        Assert.True(sentiment.GetProperty("parity").GetBoolean());
        Assert.Equal("ok", sentiment.GetProperty("reason").GetString());
    }

    [Fact]
    public void Promote_Sentiment_Accept_ShadowToActive_NoClamps()
    {
        string root = RepoRoot();
        var baseFixture = Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.candidate.json");
        var baseline = WriteConfig(baseFixture, MakeShadow);
        var candidate = WriteConfig(baseFixture, MakeNoClampActive);
        var res = RunPromote(baseline, candidate);
        if (res.ExitCode != 0)
            Assert.Fail($"Expected accept (shadow->active benign) exit 0 got {res.ExitCode}\nSTDOUT\n{res.Stdout}\nSTDERR\n{res.Stderr}");
        var rootEl = ExtractResult(res.Stdout);
        var sentiment = rootEl.GetProperty("sentiment");
        Assert.Equal("shadow", sentiment.GetProperty("baseline_mode").GetString());
        Assert.Equal("active", sentiment.GetProperty("candidate_mode").GetString());
        Assert.True(sentiment.GetProperty("parity").GetBoolean());
        Assert.Equal("ok", sentiment.GetProperty("reason").GetString());
    }

    [Fact]
    public void Promote_Sentiment_Reject_ActiveVsOff()
    {
        string root = RepoRoot();
        var baseFixture = Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.candidate.json");
        var baseline = WriteConfig(baseFixture, MakeClampActive);
        var candidate = WriteConfig(baseFixture, MakeOff);
        var res = RunPromote(baseline, candidate);
        if (res.ExitCode != 2)
            Assert.Fail($"Expected reject exit 2 got {res.ExitCode}\nSTDOUT\n{res.Stdout}\nSTDERR\n{res.Stderr}");
        var rootEl = ExtractResult(res.Stdout);
        var sentiment = rootEl.GetProperty("sentiment");
        Assert.False(sentiment.GetProperty("parity").GetBoolean());
        Assert.Equal("sentiment_mismatch", sentiment.GetProperty("reason").GetString());
        Assert.Equal("sentiment_mismatch", rootEl.GetProperty("reason").GetString());
        // diff_hint may be empty if early mode diff; ensure mismatch captured
    }

    [Fact]
    public void Promote_Sentiment_Reject_ActiveVsActive_AppliedCountMismatch()
    {
        string root = RepoRoot();
        var baseFixture = Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.candidate.json");
        var baseline = WriteConfig(baseFixture, MakeClampActive); // very sensitive
        // Candidate with slightly less sensitive threshold to reduce applied events count
        var candidate = WriteConfig(baseFixture, node => {
            node["featureFlags"] = new System.Text.Json.Nodes.JsonObject { ["sentiment"] = "active", ["riskProbe"] = "disabled" };
            node["sentimentConfig"] = new System.Text.Json.Nodes.JsonObject { ["window"] = 5, ["volGuardSigma"] = 0.1 }; // fewer clamps
        });
        var res = RunPromote(baseline, candidate);
        if (res.ExitCode != 2)
            Assert.Fail($"Expected reject exit 2 got {res.ExitCode}\nSTDOUT\n{res.Stdout}\nSTDERR\n{res.Stderr}");
        var rootEl = ExtractResult(res.Stdout);
        var sentiment = rootEl.GetProperty("sentiment");
        Assert.False(sentiment.GetProperty("parity").GetBoolean());
        Assert.Equal("sentiment_mismatch", sentiment.GetProperty("reason").GetString());
        Assert.Equal("sentiment_mismatch", rootEl.GetProperty("reason").GetString());
        var diffHint = sentiment.GetProperty("diff_hint").GetString();
        Assert.False(string.IsNullOrWhiteSpace(diffHint), "diff_hint should describe mismatch");
    }

    [Fact]
    public void Promote_Sentiment_CultureInvariant_Active()
    {
        string root = RepoRoot();
        var baseFixture = Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.candidate.json");
        var baseline = WriteConfig(baseFixture, MakeClampActive);
        var candidate = WriteConfig(baseFixture, MakeClampActive);
        var res = RunPromote(baseline, candidate, culture: "de-DE");
        if (res.ExitCode != 0)
            Assert.Fail($"Expected culture accept exit 0 got {res.ExitCode}\nSTDOUT\n{res.Stdout}\nSTDERR\n{res.Stderr}");
        var rootEl = ExtractResult(res.Stdout);
        var sentiment = rootEl.GetProperty("sentiment");
        Assert.Equal("active", sentiment.GetProperty("baseline_mode").GetString());
        Assert.Equal("active", sentiment.GetProperty("candidate_mode").GetString());
        Assert.True(sentiment.GetProperty("parity").GetBoolean());
        Assert.Equal("ok", sentiment.GetProperty("reason").GetString());
    }
}
