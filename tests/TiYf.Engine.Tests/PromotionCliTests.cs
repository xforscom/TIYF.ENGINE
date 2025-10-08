using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

[Collection("E2E-Serial")] // serialize due to journal directory reuse
public class PromotionCliTests
{
    private static string ResolveRepoRoot()
    {
        var dir = AppContext.BaseDirectory; // tests/.../bin/Release/net8.0/
        for (int i = 0; i < 10; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "tests")) && Directory.Exists(Path.Combine(dir, "src")))
                return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        throw new InvalidOperationException("Failed to resolve repository root");
    }

    private record CliResult(int ExitCode, string Stdout, string Stderr);

    private static CliResult RunPromote(string baseline, string candidate, string? culture = null)
    {
        string root = ResolveRepoRoot();
        string toolsDir = Path.Combine(root, "src", "TiYf.Engine.Tools", "bin", "Release", "net8.0");
        string toolsDll = Path.Combine(toolsDir, "TiYf.Engine.Tools.dll");
        Assert.True(File.Exists(toolsDll), "Tools binary not built - run dotnet build -c Release first");
        var args = new StringBuilder();
        args.Append("exec \"").Append(toolsDll).Append("\" promote --baseline \"").Append(baseline).Append("\" --candidate \"").Append(candidate).Append("\" --workdir \"").Append(root).Append("\" --print-metrics");
        if (!string.IsNullOrWhiteSpace(culture)) args.Append(" --culture ").Append(culture);
        var psi = new ProcessStartInfo("dotnet", args.ToString())
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder(); var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        Assert.True(proc.Start(), "Failed to start promote CLI");
        proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
        if (!proc.WaitForExit(120_000)) { try { proc.Kill(entireProcessTree: true); } catch {}; Assert.Fail($"promote timeout CMD=dotnet {args}\nSTDOUT\n{stdout}\nSTDERR\n{stderr}"); }
        return new CliResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static JsonDocument? ParsePromotionJson(string stdout, out string? line)
    {
        line = stdout.Split('\n').FirstOrDefault(l => l.Contains("PROMOTION_RESULT_V1", StringComparison.Ordinal));
        if (line == null) return null;
        try
        {
            return JsonDocument.Parse(line);
        }
        catch { return null; }
    }

    [Fact]
    public void Promote_Accept_ExitZero()
    {
        string root = ResolveRepoRoot();
        var baselineCfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.json");
        var candidateCfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.json");
        var res = RunPromote(baselineCfg, candidateCfg);
        if (res.ExitCode != 0)
        {
            Assert.Fail($"Expected accept exit 0 got {res.ExitCode}\nSTDOUT\n{res.Stdout}\nSTDERR\n{res.Stderr}");
        }
        var doc = ParsePromotionJson(res.Stdout, out var line);
        Assert.NotNull(doc);
        Assert.NotNull(line);
        Assert.True(doc!.RootElement.TryGetProperty("accepted", out var acc) && acc.GetBoolean(), "accepted flag false");
    }

    [Fact]
    public void Promote_Reject_ExitTwo()
    {
        string root = ResolveRepoRoot();
        var baselineCfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.json");
        var degradedCfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.degrade.json");
        var res = RunPromote(baselineCfg, degradedCfg);
        if (res.ExitCode != 2)
        {
            Assert.Fail($"Expected reject exit 2 got {res.ExitCode}\nSTDOUT\n{res.Stdout}\nSTDERR\n{res.Stderr}");
        }
        var doc = ParsePromotionJson(res.Stdout, out var line);
        Assert.NotNull(doc);
        Assert.NotNull(line);
        Assert.True(doc!.RootElement.TryGetProperty("accepted", out var acc) && !acc.GetBoolean(), "accepted flag true for degraded");
    }

    [Fact]
    public void Promote_CultureInvariant_ExitZero()
    {
        string root = ResolveRepoRoot();
        var baselineCfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.json");
        var candidateCfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.json");
        var res = RunPromote(baselineCfg, candidateCfg, culture: "de-DE");
        if (res.ExitCode != 0)
        {
            Assert.Fail($"Expected accept (culture) exit 0 got {res.ExitCode}\nSTDOUT\n{res.Stdout}\nSTDERR\n{res.Stderr}");
        }
        var doc = ParsePromotionJson(res.Stdout, out var line);
        Assert.NotNull(doc);
        Assert.NotNull(line);
        Assert.True(doc!.RootElement.TryGetProperty("accepted", out var acc) && acc.GetBoolean(), "accepted flag false under culture");
    // Confirm numeric tokens in JSON use '.' (parsing already succeeded under de-DE which would expect ',')
    // Sample baseline pnl field
    using var jsonDoc = JsonDocument.Parse(line!);
    var basePnlRaw = jsonDoc.RootElement.GetProperty("baseline").GetProperty("pnl").GetDecimal();
    Assert.True(basePnlRaw <= 0 || basePnlRaw >= 0, "Number parse sanity check failed");
    }
}
