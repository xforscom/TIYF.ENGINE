using System;using System.IO;using System.Linq;using System.Text;using System.Text.Json;using System.Globalization;using Xunit;

namespace TiYf.Engine.Tools.Tests;

public class PenaltyScaffoldTests
{
    private string Temp(string name,string content){ var p=Path.Combine(Path.GetTempPath(),$"pen_{Guid.NewGuid():N}_{name}"); File.WriteAllText(p,content,new UTF8Encoding(encoderShouldEmitUTF8Identifier:false)); return p; }

    private (string cfg,string instruments,string ticks) BuildConfig(bool enablePenalty,bool forcePenalty)
    {
        var instrumentsCsv = "symbol\nEURUSD\n"; var instPath = Temp("inst.csv", instrumentsCsv);
        var ticksCsv = new StringBuilder();
        ticksCsv.AppendLine("utc_ts,price,vol");
        var start = new DateTime(2025,1,2,0,0,0,DateTimeKind.Utc);
        for (int i=0;i<3;i++) ticksCsv.AppendLine(start.AddMinutes(i).ToString("O")+",1.1000,1");
        var ticksPath = Temp("ticks.csv", ticksCsv.ToString());
        var ffPenalty = enablePenalty ? ("\"penalty\":\"shadow\",") : string.Empty;
    var penaltyCfg = forcePenalty ? ",\"penaltyConfig\":{\"forcePenalty\":true}" : string.Empty;
    var ciScaffold = enablePenalty && forcePenalty ? ",\"ciPenaltyScaffold\":true" : string.Empty;
    var cfgJson = $"{{\n  \"SchemaVersion\":\"1.3.0\",\n  \"RunId\":\"{Guid.NewGuid():N}\",\n  \"InstrumentFile\":\"{instPath.Replace("\\","/")}\",\n  \"InputTicksFile\":\"{ticksPath.Replace("\\","/")}\",\n  \"JournalRoot\":\"journals/M0\",\n  \"featureFlags\":{{{ffPenalty}\"sentiment\":\"off\"}}{penaltyCfg}{ciScaffold}\n}}";
        var cfgPath = Temp("cfg.json", cfgJson);
        return (cfgPath,instPath,ticksPath);
    }

    private static string FindSolutionRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !File.Exists(Path.Combine(dir, "TiYf.Engine.sln")))
        {
            var parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }
        return dir ?? Directory.GetCurrentDirectory();
    }

    private static bool TryParseKeyValue(string line, string key, out string value)
    {
        value = null!;
        if (!line.StartsWith(key, StringComparison.Ordinal)) return false;
        var i = line.IndexOf('=');
        if (i < 0 || i + 1 >= line.Length) return false;
        value = line[(i + 1)..].Trim();
        return value.Length > 0;
    }

    private record Pen(string symbol, DateTime ts, decimal original_units, decimal adjusted_units, decimal penalty_scalar, string reason);

    private static Pen[] LoadPenaltyPayloads(string eventsPath)
    {
        var lines = File.ReadAllLines(eventsPath);
        var outList = new System.Collections.Generic.List<Pen>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("schema_version=", StringComparison.Ordinal)) continue;
            if (line.StartsWith("sequence,", StringComparison.Ordinal)) continue;
            var parts = line.Split(',', 4);
            if (parts.Length < 4) continue;
            var evt = parts[2];
            if (!evt.Equals("PENALTY_APPLIED_V1", StringComparison.Ordinal)) continue;
            var payloadRaw = parts[3].Trim();
            if (payloadRaw.StartsWith('"')) payloadRaw = payloadRaw.Substring(1, payloadRaw.Length - 2).Replace("\"\"", "\"");
            using var doc = JsonDocument.Parse(payloadRaw);
            var root = doc.RootElement;
            var symbol = root.GetProperty("symbol").GetString()!;
            var ts = DateTime.Parse(root.GetProperty("ts").GetString()!, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            var orig = root.GetProperty("original_units").GetDecimal();
            var adj = root.GetProperty("adjusted_units").GetDecimal();
            var scalar = root.GetProperty("penalty_scalar").GetDecimal();
            var reason = root.GetProperty("reason").GetString()!;
            outList.Add(new Pen(symbol, ts, orig, adj, scalar, reason));
        }
        return outList
            .OrderBy(p => p.ts)
            .ThenBy(p => p.symbol, StringComparer.Ordinal)
            .ToArray();
    }

    private string RunSim(string cfgPath,string runId, string? expectedMode = null, bool? expectForce = null, bool? expectCiScaffold = null)
    {
        var root = FindSolutionRoot();
        var dll = Path.Combine(root,"src","TiYf.Engine.Sim","bin","Release","net8.0","TiYf.Engine.Sim.dll");
        Assert.True(File.Exists(dll),$"Sim DLL missing; build Release first at {dll}");
        // Determinism: ensure we don't reuse a previous journal run folder from earlier local runs
    var runDir = Path.Combine(root, "journals", "M0", runId);
        if (Directory.Exists(runDir))
        {
            try { Directory.Delete(runDir, recursive: true); } catch { /* best effort cleanup */ }
        }
    var psi = new System.Diagnostics.ProcessStartInfo("dotnet",$"exec \"{dll}\" --config \"{cfgPath}\" --run-id {runId} --verbose")
        { RedirectStandardError=true,RedirectStandardOutput=true,UseShellExecute=false,CreateNoWindow=true, WorkingDirectory = root };
    var p = System.Diagnostics.Process.Start(psi)!; p.WaitForExit(15000);
    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    // Sanity: confirm simulator parsed penalty flags as expected for this test
    var lines = stdout.Split(new[]{'\r','\n'}, StringSplitOptions.RemoveEmptyEntries);
    string? penaltyMode = null; string? eventsPath = null; bool forceFlag = false; bool ciFlag = false;
    foreach (var line in lines)
    {
        if (TryParseKeyValue(line, "PENALTY_MODE_RESOLVED", out var v))
        {
            penaltyMode = v.Split(' ')[0];
            // also check inline flags
            forceFlag = line.IndexOf("force=true", StringComparison.OrdinalIgnoreCase) >= 0;
            ciFlag = line.IndexOf("ci_scaffold=true", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        if (TryParseKeyValue(line, "JOURNAL_DIR_EVENTS", out var pth)) eventsPath = pth;
    }
    if (!string.IsNullOrWhiteSpace(expectedMode))
    {
        if (string.IsNullOrWhiteSpace(penaltyMode))
        {
            var head = string.Join(Environment.NewLine, lines.Take(20));
            Assert.Fail($"Expected penalty mode '{expectedMode}', but none was resolved. First stdout lines:\n{head}");
        }
        Assert.Equal(expectedMode, penaltyMode);
    }
    if (expectForce.HasValue) Assert.Equal(expectForce.Value, forceFlag);
    if (expectCiScaffold.HasValue) Assert.Equal(expectCiScaffold.Value, ciFlag);
        Assert.Equal(0,p.ExitCode);
        // Prefer the simulator-reported events path for precision
        eventsPath ??= Path.Combine(runDir, "events.csv");
        // Rare flake guard: if penalty expected but not found (filesystem lag or race), retry once after cleanup
        if (!File.Exists(eventsPath) || !File.ReadAllLines(eventsPath).Any(l => l.Contains("PENALTY_APPLIED_V1")))
        {
            try { if (Directory.Exists(runDir)) Directory.Delete(runDir, true); } catch { }
            var p2 = System.Diagnostics.Process.Start(psi)!; p2.WaitForExit(15000);
            Assert.Equal(0, p2.ExitCode);
            eventsPath = Path.Combine(runDir, "events.csv");
        }
        return eventsPath;
    }

    [Fact]
    public void Penalty_Disabled_NoEvents()
    {
        var (cfg,_,_) = BuildConfig(false,false);
    var eventsPath = RunSim(cfg,"PENOFF", expectedMode: "off", expectForce: false, expectCiScaffold: false);
        var hasPenalty = File.ReadAllLines(eventsPath).Any(l=>l.Contains("PENALTY_APPLIED_V1"));
        Assert.False(hasPenalty);
    }

    [Fact]
    public void Penalty_Enabled_Emits_Deterministic()
    {
        var (cfg,_,_) = BuildConfig(true,true);
    var e1 = RunSim(cfg,"PENON1", expectedMode: "shadow", expectForce: true, expectCiScaffold: true);
    var e2 = RunSim(cfg,"PENON2", expectedMode: "shadow", expectForce: true, expectCiScaffold: true);
        var p1 = LoadPenaltyPayloads(e1);
        var p2 = LoadPenaltyPayloads(e2);
        Assert.True(p1.Length >= 1, "Expected at least one penalty in run A");
        Assert.True(p2.Length >= 1, "Expected at least one penalty in run B");
        // Determinism: payloads equal when sorted by (ts,symbol)
        Assert.Equal(p1.Length, p2.Length);
        for (int i=0;i<p1.Length;i++)
        {
            Assert.Equal(p1[i].symbol, p2[i].symbol);
            Assert.Equal(p1[i].ts, p2[i].ts);
            Assert.Equal(p1[i].original_units, p2[i].original_units);
            Assert.Equal(p1[i].adjusted_units, p2[i].adjusted_units);
            Assert.Equal(p1[i].penalty_scalar, p2[i].penalty_scalar);
            Assert.Equal(p1[i].reason, p2[i].reason);
        }
    }

    [Fact]
    public void Penalty_Formatting_Invariant()
    {
        var (cfg,_,_) = BuildConfig(true,true);
    var ev = RunSim(cfg,"PENFMT", expectedMode: "shadow", expectForce: true, expectCiScaffold: true);
        var payloads = LoadPenaltyPayloads(ev);
        Assert.True(payloads.Length >= 1, "Expected at least one penalty payload");
        // Check formatting invariants using raw JSON to ensure non-scientific representations
        var lines = File.ReadAllLines(ev);
        foreach (var line in lines)
        {
            if (!line.Contains("PENALTY_APPLIED_V1")) continue;
            var parts = line.Split(',',4);
            if (parts.Length < 4) continue;
            var payloadRaw = parts[3].Trim();
            if (payloadRaw.StartsWith('"')) payloadRaw = payloadRaw.Substring(1, payloadRaw.Length - 2).Replace("\"\"", "\"");
            using var doc = JsonDocument.Parse(payloadRaw);
            var root = doc.RootElement;
            string rawOrig = root.GetProperty("original_units").GetRawText();
            string rawAdj = root.GetProperty("adjusted_units").GetRawText();
            string rawScalar = root.GetProperty("penalty_scalar").GetRawText();
            Assert.DoesNotContain("E", rawOrig, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("E", rawAdj, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("E", rawScalar, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(',', rawOrig);
            Assert.DoesNotContain(',', rawAdj);
            Assert.DoesNotContain(',', rawScalar);
        }
        foreach (var pen in payloads)
        {
            Assert.True(pen.original_units >= 1, "original_units must be >= 1");
            Assert.True(pen.adjusted_units >= 1, "adjusted_units must be >= 1");
            Assert.True(pen.penalty_scalar > 0m && pen.penalty_scalar <= 1m, "penalty_scalar must be (0,1]");
        }
    }
}
