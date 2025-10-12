using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TiYf.Engine.Tools.Tests;

public class PenaltyScaffoldTests
{
    private string Temp(string name, string content) { var p = Path.Combine(Path.GetTempPath(), $"pen_{Guid.NewGuid():N}_{name}"); File.WriteAllText(p, content, Encoding.UTF8); return p; }

    private (string cfg, string instruments, string ticks) BuildConfig(bool enablePenalty, bool forcePenalty)
    {
        var instrumentsCsv = "symbol\nEURUSD\n"; var instPath = Temp("inst.csv", instrumentsCsv);
        var ticksCsv = new StringBuilder();
        ticksCsv.AppendLine("utc_ts,price,vol");
        var start = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 3; i++) ticksCsv.AppendLine(start.AddMinutes(i).ToString("O") + ",1.1000,1");
        var ticksPath = Temp("ticks.csv", ticksCsv.ToString());
        var ffPenalty = enablePenalty ? ("\"penalty\":\"shadow\",") : string.Empty;
        var penaltyCfg = forcePenalty ? ",\"penaltyConfig\":{\"forcePenalty\":true}" : string.Empty;
        var cfgJson = $"{{\n  \"schemaVersion\":\"1.2.0\",\n  \"instrumentFile\":\"{instPath.Replace("\\", "/")}\",\n  \"inputTicksFile\":\"{ticksPath.Replace("\\", "/")}\",\n  \"journalRoot\":\"journals/M0\",\n  \"featureFlags\":{{{ffPenalty}\"sentiment\":\"off\"}}{penaltyCfg}\n}}";
        var cfgPath = Temp("cfg.json", cfgJson);
        return (cfgPath, instPath, ticksPath);
    }

    private string RunSim(string cfgPath, string runId)
    {
        var dll = Path.Combine(Directory.GetCurrentDirectory(), "src", "TiYf.Engine.Sim", "bin", "Release", "net8.0", "TiYf.Engine.Sim.dll");
        Assert.True(File.Exists(dll), "Sim DLL missing; build Release first");
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"exec \"{dll}\" --config \"{cfgPath}\" --run-id {runId} --quiet")
        { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        var p = System.Diagnostics.Process.Start(psi)!; p.WaitForExit(15000);
        Assert.Equal(0, p.ExitCode);
        var journalDir = Directory.GetDirectories(Path.Combine(Directory.GetCurrentDirectory(), "journals", "M0"), $"*{runId}*").First();
        return Path.Combine(journalDir, "events.csv");
    }

    [Fact]
    public void Penalty_Disabled_NoEvents()
    {
        var (cfg, _, _) = BuildConfig(false, false);
        var eventsPath = RunSim(cfg, "PENOFF");
        var hasPenalty = File.ReadAllLines(eventsPath).Any(l => l.Contains("PENALTY_APPLIED_V1"));
        Assert.False(hasPenalty);
    }

    [Fact]
    public void Penalty_Enabled_Emits_Deterministic()
    {
        var (cfg, _, _) = BuildConfig(true, true);
        var e1 = RunSim(cfg, "PENON1");
        var e2 = RunSim(cfg, "PENON2");
        string ExtractLine(string p) => File.ReadAllLines(p).FirstOrDefault(l => l.Contains("PENALTY_APPLIED_V1")) ?? string.Empty;
        var l1 = ExtractLine(e1); var l2 = ExtractLine(e2);
        Assert.NotEmpty(l1);
        Assert.Equal(l1.Split(',', 2)[1], l2.Split(',', 2)[1]); // ignore sequence divergence; compare rest of line
    }

    [Fact]
    public void Penalty_Formatting_Invariant()
    {
        var (cfg, _, _) = BuildConfig(true, true);
        var ev = RunSim(cfg, "PENFMT");
        var line = File.ReadAllLines(ev).FirstOrDefault(l => l.Contains("PENALTY_APPLIED_V1"));
        Assert.NotNull(line);
        // payload is last field quoted JSON
        var parts = line!.Split(',', 4); Assert.True(parts.Length >= 4);
        var payloadRaw = parts[3].Trim(); if (payloadRaw.StartsWith('"')) payloadRaw = payloadRaw.Substring(1, payloadRaw.Length - 2).Replace("\"\"", "\"");
        using var doc = JsonDocument.Parse(payloadRaw);
        var scalar = doc.RootElement.GetProperty("penalty_scalar").GetDecimal();
        Assert.True(scalar >= 0m && scalar <= 1m);
        Assert.DoesNotContain('E', payloadRaw);
    }
}
