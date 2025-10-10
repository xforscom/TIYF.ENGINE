using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace TiYf.Engine.Tools.Tests;

public class PenaltyParityAndDeterminismTests
{
    private static string SimDll()
    {
        var root = Directory.GetCurrentDirectory();
        var dll = Path.Combine(root, "src","TiYf.Engine.Sim","bin","Release","net8.0","TiYf.Engine.Sim.dll");
        if (!File.Exists(dll))
        {
            var dbg = Path.Combine(root, "src","TiYf.Engine.Sim","bin","Debug","net8.0","TiYf.Engine.Sim.dll");
            if (File.Exists(dbg)) return dbg;
        }
        return dll;
    }

    private static string ToolsDll()
    {
        var root = Directory.GetCurrentDirectory();
        var rel = Path.Combine(root, "src","TiYf.Engine.Tools","bin","Release","net8.0","TiYf.Engine.Tools.dll");
        if (File.Exists(rel)) return rel;
        var dbg = Path.Combine(root, "src","TiYf.Engine.Tools","bin","Debug","net8.0","TiYf.Engine.Tools.dll");
        return dbg;
    }

    private static string WriteCfg(string penaltyMode, bool forcePenalty, string sentimentMode)
    {
        var root = Directory.GetCurrentDirectory();
        var src = Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(src))!.AsObject();
        if (!node.TryGetPropertyValue("featureFlags", out var ff) || ff is not System.Text.Json.Nodes.JsonObject)
            node["featureFlags"] = new System.Text.Json.Nodes.JsonObject();
        var ffObj = node["featureFlags"]!.AsObject();
        ffObj["penalty"] = penaltyMode;
        ffObj["sentiment"] = sentimentMode;
        node["penaltyConfig"] = new System.Text.Json.Nodes.JsonObject { ["forcePenalty"] = forcePenalty };
        var tmp = Path.Combine(Path.GetTempPath(), $"pen_t_{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, node.ToJsonString());
        return tmp;
    }

    private static (string ev, string tr) RunSim(string cfg, string runId)
    {
        var dll = SimDll();
        Assert.True(File.Exists(dll), $"Sim DLL missing: {dll}");
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"exec \"{dll}\" --config \"{cfg}\" --run-id {runId} --quiet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory.GetCurrentDirectory()
        };
        var p = System.Diagnostics.Process.Start(psi)!; p.WaitForExit(60000);
        Assert.Equal(0, p.ExitCode);
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "journals","M0", $"M0-RUN-{runId}");
        return (Path.Combine(dir, "events.csv"), Path.Combine(dir, "trades.csv"));
    }

    private static string ExecTools(string args)
    {
        var dll = ToolsDll();
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"exec \"{dll}\" {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory.GetCurrentDirectory()
        };
        var p = System.Diagnostics.Process.Start(psi)!; p.WaitForExit(60000);
        return $"EXIT={p.ExitCode}\n" + p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    }

    [Fact]
    public void Penalty_Active_Vs_Shadow_Parity_TradesEqual()
    {
        var cfgShadow = WriteCfg("shadow", forcePenalty: true, sentimentMode: "off");
        var cfgActive = WriteCfg("active", forcePenalty: true, sentimentMode: "off");
        var (evS, trS) = RunSim(cfgShadow, "PEN-S");
        var (evA, trA) = RunSim(cfgActive, "PEN-A");
        var outp = ExecTools($"verify parity --events-a \"{evS}\" --events-b \"{evA}\" --trades-a \"{trS}\" --trades-b \"{trA}\"");
        // Penalty lines may differ, but trades should match
        Assert.Contains("PARITY trades: OK", outp);
    }

    [Fact]
    public void Penalty_Active_Determinism_AB()
    {
        var cfgActive = WriteCfg("active", forcePenalty: true, sentimentMode: "off");
        var (ev1, tr1) = RunSim(cfgActive, "PEN-A1");
        var (ev2, tr2) = RunSim(cfgActive, "PEN-A2");
        var outp = ExecTools($"verify parity --events-a \"{ev1}\" --events-b \"{ev2}\" --trades-a \"{tr1}\" --trades-b \"{tr2}\"");
        Assert.Contains("EXIT=0", outp);
    }
}
