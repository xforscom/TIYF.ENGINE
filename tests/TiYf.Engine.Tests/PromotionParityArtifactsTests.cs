using System.Text.Json;
using Xunit;

// This test shells dotnet build/run and relies on locating Release binaries and writing artifacts.
// It is intentionally covered by dedicated CI (nightly-canary) with parity uploads and invariants.
// Marked [Skip] here to avoid flaky behavior on constrained agents and local dev environments.
public class PromotionParityArtifactsTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = Directory.GetCurrentDirectory();
            // Walk up until we find the solution file
            for (int i=0;i<10;i++)
            {
                Console.WriteLine($"DEBUG RepoRoot probe level {i} dir={dir}");
                if (Directory.GetFiles(dir, "TiYf.Engine.sln", SearchOption.TopDirectoryOnly).Any()) return dir;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return Directory.GetCurrentDirectory();
        }
    }
    private string SimDll
    {
        get
        {
            // Search up to repo root for Release/net8.0/TiYf.Engine.Sim.dll
            var start = Directory.GetCurrentDirectory();
            for (int i=0;i<10;i++)
            {
                var candidates = Directory.GetFiles(start, "TiYf.Engine.Sim.dll", SearchOption.AllDirectories)
                    .Where(p => p.Contains(Path.Combine("bin","Release"), StringComparison.OrdinalIgnoreCase) && p.EndsWith(Path.Combine("net8.0","TiYf.Engine.Sim.dll"), StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (candidates.Count>0) return candidates[0];
                var parent = Directory.GetParent(start); if (parent==null) break; start = parent.FullName;
            }
            return Path.Combine(RepoRoot, "src","TiYf.Engine.Sim","bin","Release","net8.0","TiYf.Engine.Sim.dll");
        }
    }

    private (int Exit, string RunDir, string RunId) RunSim(string configPath, string runTag, string? modeOverride = null)
    {
        var buildProj = Path.Combine(RepoRoot, "src", "TiYf.Engine.Sim", "TiYf.Engine.Sim.csproj");
        Assert.True(File.Exists(buildProj), $"Sim project not found at {buildProj}");
        // Ensure Release build
        var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", $"build \"{buildProj}\" -c Release --no-restore") { WorkingDirectory = RepoRoot, RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false });
        build!.WaitForExit(60000);
        // Proceed with run via 'dotnet run'
        string tmpCfg = Path.GetTempFileName();
        var json = File.ReadAllText(configPath);
        if (modeOverride != null)
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            void Recurse(JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject();
                    bool featureFlagsWritten = false;
                    foreach (var p in el.EnumerateObject())
                    {
                        if (p.NameEquals("featureFlags"))
                        {
                            featureFlagsWritten = true;
                            writer.WritePropertyName("featureFlags");
                            writer.WriteStartObject();
                            bool sentimentWritten = false;
                            foreach (var ff in p.Value.EnumerateObject())
                            {
                                if (ff.NameEquals("sentiment"))
                                {
                                    sentimentWritten = true;
                                    writer.WriteString("sentiment", modeOverride);
                                }
                                else
                                {
                                    writer.WritePropertyName(ff.Name);
                                    ff.Value.WriteTo(writer);
                                }
                            }
                            if (!sentimentWritten) writer.WriteString("sentiment", modeOverride);
                            writer.WriteEndObject();
                        }
                        else
                        {
                            writer.WritePropertyName(p.Name); p.Value.WriteTo(writer);
                        }
                    }
                    if (!featureFlagsWritten)
                    {
                        writer.WritePropertyName("featureFlags");
                        writer.WriteStartObject();
                        writer.WriteString("sentiment", modeOverride);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
                else el.WriteTo(writer);
            }
            Recurse(doc.RootElement);
            writer.Flush();
            json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        File.WriteAllText(tmpCfg, json);
        string runId = "TEST-" + runTag + "-" + Guid.NewGuid().ToString("N").Substring(0,8);
    var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"run --project \"{buildProj}\" -c Release -- --config \"{tmpCfg}\" --run-id {runId}")
        {
            RedirectStandardOutput=true,
            RedirectStandardError=true,
            UseShellExecute=false,
            CreateNoWindow=true,
            WorkingDirectory = RepoRoot
        };
        var proc = System.Diagnostics.Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60000);
        if (!proc.HasExited) { try { proc.Kill(entireProcessTree:true);} catch{} }
        int exit = proc.ExitCode;
        if (exit != 0)
        {
            Console.WriteLine("SIM_STDOUT:\n"+stdout);
            Console.WriteLine("SIM_STDERR:\n"+stderr);
        }
        string runDir = System.IO.Path.Combine(RepoRoot, "journals", "M0", $"M0-RUN-{runId}");
        return (exit, runDir, runId);
    }

    [Fact(Skip = "Covered by nightly-canary workflow; environment-dependent run + artifact paths")]
    public void ParityArtifacts_AreGeneratedAndConsistent()
    {
        // Use existing backtest-m0 fixture config (assumed present in repo) or skip if missing
        var config = Path.Combine(RepoRoot, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json");
        Assert.True(File.Exists(config), $"Fixture config missing at {config}");
        var off = RunSim(config, "off", "off");
    Assert.True(off.Exit==0, $"Off run failed exit={off.Exit}");
        var shadow = RunSim(config, "shadow", "shadow");
    Assert.True(shadow.Exit==0, $"Shadow run failed exit={shadow.Exit}");
        var active = RunSim(config, "active", "active");
    Assert.True(active.Exit==0, $"Active run failed exit={active.Exit}");

        void AssertHashes(string runDir, out string eventsSha, out string tradesSha, out int applied, out int penalty)
        {
            var hashFile = Path.Combine(RepoRoot, "artifacts", "parity", Path.GetFileName(runDir).Replace("M0-RUN-", string.Empty), "hashes.txt");
            Assert.True(File.Exists(hashFile), $"hashes.txt missing for {runDir}");
            var dict = File.ReadAllLines(hashFile).Where(l=>l.Contains('=')).Select(l=>l.Split('=')).ToDictionary(p=>p[0], p=>p[1]);
            eventsSha = dict.GetValueOrDefault("events_sha", string.Empty);
            tradesSha = dict.GetValueOrDefault("trades_sha", string.Empty);
            applied = int.Parse(dict.GetValueOrDefault("applied_count", "0"));
            penalty = int.Parse(dict.GetValueOrDefault("penalty_count", "0"));
            Assert.False(string.IsNullOrWhiteSpace(eventsSha));
            Assert.False(string.IsNullOrWhiteSpace(tradesSha));
        }

        AssertHashes(off.RunDir, out var offE, out var offT, out var offApplied, out var offPen);
        AssertHashes(shadow.RunDir, out var shE, out var shT, out var shApplied, out var shPen);
        AssertHashes(active.RunDir, out var actE, out var actT, out var actApplied, out var actPen);

        // Off vs Shadow parity expectation (trades identical when no sentiment influence)
        Assert.Equal(offT, shT);
        // Active may diverge only if applied or penalty counts > 0
        if (actT != offT)
        {
            Assert.True(actApplied > 0 || actPen > 0, "Active trades hash diverged without applied or penalty events");
        }
    }
}
