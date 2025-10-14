using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

[Collection("E2E-Serial")] // serialize promotion runs
public class PromotionCliPenaltyTests : IDisposable
{
    private record CliResult(int ExitCode, string Stdout, string Stderr);
    private readonly List<string> _tempFiles = new();

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

    private string WritePenaltyConfig(string srcCfg, string penaltyMode, bool forcePenalty, string sentimentMode = "off")
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
        if (forcePenalty)
        {
            node["ciPenaltyScaffold"] = true;
        }
        var tmp = Path.Combine(Path.GetTempPath(), $"promo_pen_{penaltyMode}_{sentimentMode}_{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, node.ToJsonString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _tempFiles.Add(tmp);
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
        Assert.Equal("ok", penalty.GetProperty("parity").GetString());
        Assert.Equal("ok", penalty.GetProperty("reason").GetString());
        Assert.Equal(string.Empty, penalty.GetProperty("diff_hint").GetString());
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
        var reason = result.GetProperty("reason").GetString();
        var penalty = result.GetProperty("penalty");
        var penaltyParity = penalty.GetProperty("parity").GetString();
        Assert.Equal("penalty_mismatch", reason);
        Assert.Equal("mismatch", penaltyParity);
        Assert.Equal("penalty_mismatch", penalty.GetProperty("reason").GetString());
        var diffHint = penalty.GetProperty("diff_hint").GetString();
        Assert.False(string.IsNullOrWhiteSpace(diffHint));
        var match = Regex.Match(diffHint!.Trim(), "^penalty seq: baseline=(\\d+) candidate=(\\d+)$", RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"penalty diff hint did not match contract. value='{diffHint}'");
        var baselineSeq = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var candidateSeq = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        Assert.NotEqual(baselineSeq, candidateSeq);
        // Back-compat guard: tolerate legacy parity_mismatch reason only when penalty metadata signals mismatch
        if (reason == "parity_mismatch") Assert.Equal("mismatch", penaltyParity);
    }

    [Fact]
    public void Promotion_ResultIncludesPenaltyDiffHint_OnMismatch()
    {
        var root = RepoRoot();
        var baseSrc = Path.Combine(root, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json");
        var baseline = WritePenaltyConfig(baseSrc, penaltyMode: "active", forcePenalty: true, sentimentMode: "off");
        var candidate = WritePenaltyConfig(baseSrc, penaltyMode: "active", forcePenalty: true, sentimentMode: "shadow");
        var res = RunPromote(baseline, candidate);
        var result = ExtractResult(res.Stdout);
        var penalty = result.GetProperty("penalty");
        Assert.Equal("mismatch", penalty.GetProperty("parity").GetString());
        var hint = penalty.GetProperty("diff_hint").GetString();
        Assert.False(string.IsNullOrWhiteSpace(hint));
        var match = Regex.Match(hint!.Trim(), "^penalty seq: baseline=(\\d+) candidate=(\\d+)$", RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"penalty diff hint did not match contract. value='{hint}'");
        var baselineSeq = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var candidateSeq = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        Assert.NotEqual(baselineSeq, candidateSeq);
        Assert.Contains("penalty", result.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var tmp in _tempFiles)
        {
            try
            {
                if (!string.IsNullOrEmpty(tmp) && File.Exists(tmp)) File.Delete(tmp);
            }
            catch
            {
                // best-effort cleanup; ignore failures to avoid masking test results
            }
        }
    }
}
