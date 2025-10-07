using System.Diagnostics;
using System.Text;
using TiYf.Engine.Core;
using Xunit;

namespace TiYf.Engine.Tests;

public class DataQaTests
{
    private static string FindSolutionRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !File.Exists(Path.Combine(dir, "TiYf.Engine.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir ?? Directory.GetCurrentDirectory();
    }

    [Fact]
    public void CleanFixture_DataQa_PassesAndBarsEmit()
    {
        var root = FindSolutionRoot();
        var cfgPath = Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.json");
        Assert.True(File.Exists(cfgPath));
        var tmpDir = Path.Combine(Path.GetTempPath(), "dataqa-pass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        // Deterministic JSON modification using JsonNode to avoid malformed concatenation
        var baselineText = File.ReadAllText(cfgPath);
        var rootNode = System.Text.Json.Nodes.JsonNode.Parse(baselineText)!.AsObject();
        // Overwrite or add dataQA block
        rootNode["dataQA"] = new System.Text.Json.Nodes.JsonObject{
            ["enabled"] = true,
            ["maxMissingBarsPerInstrument"] = 999,
            ["allowDuplicates"] = true,
            ["spikeZ"] = 50.0,
            ["repair"] = new System.Text.Json.Nodes.JsonObject{
                ["forwardFillBars"] = 1,
                ["dropSpikes"] = false
            }
        };
        var modCfg = Path.Combine(tmpDir, "config.json");
        var outText = rootNode.ToJsonString(new System.Text.Json.JsonSerializerOptions{WriteIndented=false});
        File.WriteAllText(modCfg, outText);
    Console.WriteLine("DATA_QA_PASS_TEST_CONFIG=" + outText);
        var runId = RunSim(root, modCfg);
        var journalBase = Path.Combine(root, "journals","M0",$"M0-RUN-{runId}");
        Assert.True(File.Exists(Path.Combine(journalBase, "events.csv")));
        var lines = File.ReadAllLines(Path.Combine(journalBase, "events.csv"));
        // Locate DATA_QA_SUMMARY_V1 line and parse JSON payload for passed=true
    // Diagnostics: capture any issue lines for investigation
    var issueLines = lines.Where(l => l.Contains(",DATA_QA_ISSUE_V1,")).Take(5).ToList();
    var summaryLine = lines.FirstOrDefault(l => l.Contains(",DATA_QA_SUMMARY_V1,"));
        Assert.False(string.IsNullOrEmpty(summaryLine), "DATA_QA_SUMMARY_V1 line not found");
        if (summaryLine != null)
        {
            var parts = summaryLine.Split(',',4);
            Assert.True(parts.Length == 4, "Unexpected CSV field count in summary line");
            var payloadCsvField = parts[3];
            var json = payloadCsvField.Trim().Trim('"').Replace("\"\"", "\""); // unescape doubled quotes (CSV quotes doubled)
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var rootEl = doc.RootElement;
            // Accept either pass or fail here; objective is deterministic JSON shape and continued bar emission.
            Assert.True(rootEl.TryGetProperty("passed", out var passedProp), "Missing 'passed' property in summary payload");
            Assert.True(passedProp.ValueKind==System.Text.Json.JsonValueKind.True, "Expected passed=true after tolerance filtering");
        }
    Assert.Empty(issueLines); // no issues should remain after tolerance
    Assert.DoesNotContain(lines, l => l.Contains(",DATA_QA_ABORT_V1,"));
        Assert.Contains(lines, l => l.Contains(",BAR_V1,"));
    }

    [Fact]
    public void MissingBars_TriggersAbort_NoBars()
    {
        var root = FindSolutionRoot();
        var cfgPath = Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.json");
        Assert.True(File.Exists(cfgPath));
        var tmpDir = Path.Combine(Path.GetTempPath(), "dataqa-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var fixtureRoot = Path.Combine(tmpDir, "fixture");
        Directory.CreateDirectory(fixtureRoot);
        var origRoot = Path.Combine(root, "tests","fixtures","backtest_m0");
        File.Copy(Path.Combine(origRoot, "instruments.csv"), Path.Combine(fixtureRoot, "instruments.csv"));
        void CopyAndGap(string symbolFile)
        {
            var src = Path.Combine(origRoot, symbolFile);
            var dest = Path.Combine(fixtureRoot, symbolFile);
            var all = File.ReadAllLines(src).ToList();
            var removed = all.Where(l => l.Contains("2025-01-02T00:30:")).ToList();
            all = all.Where(l => !l.Contains("2025-01-02T00:30:")).ToList();
            File.WriteAllLines(dest, all);
            Assert.True(removed.Count > 0, "Expected to remove at least one tick to form gap");
        }
        CopyAndGap("ticks_EURUSD.csv");
        File.Copy(Path.Combine(origRoot, "ticks_USDJPY.csv"), Path.Combine(fixtureRoot, "ticks_USDJPY.csv"));
        File.Copy(Path.Combine(origRoot, "ticks_XAUUSD.csv"), Path.Combine(fixtureRoot, "ticks_XAUUSD.csv"));
        var cfgJsonOrig = File.ReadAllText(cfgPath);
        using var doc = System.Text.Json.JsonDocument.Parse(cfgJsonOrig);
        var cfgObj = new Dictionary<string,object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.NameEquals("data"))
            {
                var dataNode = prop.Value;
                var ticks = new Dictionary<string,string>();
                foreach (var tk in dataNode.GetProperty("ticks").EnumerateObject())
                {
                    ticks[tk.Name] = Path.Combine(fixtureRoot, tk.Value.GetString()!.Split('/').Last()).Replace('\\','/');
                }
                var dataObj = new Dictionary<string,object?>{
                    ["type"] = dataNode.GetProperty("type").GetString(),
                    ["root"] = fixtureRoot.Replace('\\','/'),
                    ["instrumentsFile"] = Path.Combine(fixtureRoot, "instruments.csv").Replace('\\','/'),
                    ["ticks"] = ticks,
                    ["window"] = new Dictionary<string,string>{ ["fromUtc"] = dataNode.GetProperty("window").GetProperty("fromUtc").GetString()!, ["toUtc"] = dataNode.GetProperty("window").GetProperty("toUtc").GetString()! }
                };
                cfgObj[prop.Name] = dataObj;
            }
            else 
            {
                // Fallback: copy raw JSON into dom by re-parsing string representation
                cfgObj[prop.Name] = System.Text.Json.JsonSerializer.Deserialize<object?>(prop.Value.GetRawText());
            }
        }
        cfgObj["dataQA"] = new Dictionary<string,object?>{
            ["enabled"] = true,
            ["maxMissingBarsPerInstrument"] = 0,
            ["allowDuplicates"] = false,
            ["spikeZ"] = 5,
            ["repair"] = new Dictionary<string,object?>{ ["forwardFillBars"] = 0, ["dropSpikes"] = true }
        };
        var finalCfg = System.Text.Json.JsonSerializer.Serialize(cfgObj, new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
        var modCfg = Path.Combine(tmpDir, "config.json");
        File.WriteAllText(modCfg, finalCfg);
    var runId = RunSim(root, modCfg);
    var journalBase = Path.Combine(root, "journals","M0",$"M0-RUN-{runId}");
        var eventsFile = Path.Combine(journalBase, "events.csv");
        Assert.True(File.Exists(eventsFile));
        var lines = File.ReadAllLines(eventsFile);
        Assert.Contains(lines, l => l.Contains(",DATA_QA_ISSUE_V1,") && l.Contains("missing_bar"));
        Assert.Contains(lines, l => l.Contains(",DATA_QA_ABORT_V1,"));
        Assert.DoesNotContain(lines, l => l.Contains(",BAR_V1,"));
        Assert.False(File.Exists(Path.Combine(journalBase, "trades.csv")));
    }

    private static string RunSim(string root, string configPath)
    {
        var simDll = Path.Combine(root, "src","TiYf.Engine.Sim","bin","Release","net8.0","TiYf.Engine.Sim.dll");
        if (!File.Exists(simDll))
        {
            var build = new ProcessStartInfo("dotnet", "build -c Release") { WorkingDirectory = root, RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false };
            var b = Process.Start(build)!; b.WaitForExit();
            Assert.True(File.Exists(simDll), "Sim DLL not built");
        }
        var runId = "dataqa-"+Guid.NewGuid().ToString("N").Substring(0,8);
        string RunOnce()
        {
            var args = $"exec \"{simDll}\" --config \"{configPath}\" --verbosity quiet --run-id {runId}";
            var psi = new ProcessStartInfo("dotnet", args) { WorkingDirectory = root, RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false };
            var p = Process.Start(psi)!;
            var stdout = new StringBuilder(); var stderr = new StringBuilder();
            p.OutputDataReceived += (_,e)=> { if (e.Data!=null) stdout.AppendLine(e.Data); }; p.BeginOutputReadLine();
            p.ErrorDataReceived += (_,e)=> { if (e.Data!=null) stderr.AppendLine(e.Data); }; p.BeginErrorReadLine();
            if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } Assert.Fail($"Sim timeout. CMD=dotnet {args}\nCWD={root}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}"); }
            if (p.ExitCode != 0)
            {
            var journalBase = Path.Combine(root, "journals","M0",$"M0-RUN-{runId}");
                Assert.Fail($"Sim non-zero exit. Code={p.ExitCode}\nCMD=dotnet {args}\nCWD={root}\nJOURNAL_DIR={journalBase}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            }
            return stdout.ToString();
        }
        RunOnce();
    var journalBase = Path.Combine(root, "journals","M0",$"M0-RUN-{runId}");
        var eventsPath = Path.Combine(journalBase, "events.csv");
        var copyA = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")+"-A.csv");
        File.Copy(eventsPath, copyA, true);
        RunOnce();
        var copyB = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")+"-B.csv");
        File.Copy(eventsPath, copyB, true);
        string Hash(string p) => CsvCanonicalizer.Sha256Hex(CsvCanonicalizer.Canonicalize(File.ReadAllBytes(p)));
        Assert.Equal(Hash(copyA), Hash(copyB));
        return runId;
    }

    // Removed duplicate RunSim overload (solutionRoot,cfgPath) to avoid ambiguity.

    private static (string EventsPath,string TradesPath) LocateJournal(string solutionRoot, string runId)
    {
        var dir = Path.Combine(solutionRoot, "journals","M0", $"M0-RUN-{runId}");
        return (Path.Combine(dir, "events.csv"), Path.Combine(dir, "trades.csv"));
    }

    [Fact]
    public void Active_CleanPass_NoAbort()
    {
        var root = FindSolutionRoot();
        var baseCfg = File.ReadAllText(Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.json"));
        var node = System.Text.Json.Nodes.JsonNode.Parse(baseCfg)!.AsObject();
        node["featureFlags"] = new System.Text.Json.Nodes.JsonObject{ ["dataQa"] = "active" };
        node["dataQA"] = new System.Text.Json.Nodes.JsonObject{
            ["enabled"] = true,
            ["maxMissingBarsPerInstrument"] = 999,
            ["allowDuplicates"] = true,
            ["spikeZ"] = 50,
            ["repair"] = new System.Text.Json.Nodes.JsonObject{ ["forwardFillBars"] = 1, ["dropSpikes"] = false }
        };
        var tmp = Path.Combine(Path.GetTempPath(), "dq-active-clean"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmp);
        var cfgPath = Path.Combine(tmp, "config.json"); File.WriteAllText(cfgPath, node.ToJsonString());
        var runId = RunSim(root, cfgPath);
        var (eventsPath, tradesPath) = LocateJournal(root, runId);
        Assert.True(File.Exists(eventsPath)); Assert.True(File.Exists(tradesPath));
        var lines = File.ReadAllLines(eventsPath);
        var summary = lines.First(l=>l.Contains(",DATA_QA_SUMMARY_V1,"));
        Assert.DoesNotContain(lines, l=>l.Contains(",DATA_QA_ABORT_V1,"));
        Assert.Contains("\"passed\":true", summary);
        Assert.Contains("\"aborted\":false", summary);
        var tolHashMatch = System.Text.RegularExpressions.Regex.Match(summary, "\\\"tolerance_profile_hash\\\":\\\"([0-9A-F]{64})\\\"");
        Assert.True(tolHashMatch.Success, "tolerance_profile_hash 64-hex not found in summary line");
        Assert.Contains(lines, l=>l.Contains(",BAR_V1,"));
    }

    [Fact]
    public void Active_ToleratedIssues_Passes()
    {
        var root = FindSolutionRoot();
        var cfgText = File.ReadAllText(Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.json"));
    var node = System.Text.Json.Nodes.JsonNode.Parse(cfgText)!.AsObject();
        node["featureFlags"] = new System.Text.Json.Nodes.JsonObject{ ["dataQa"] = "active" };
        node["dataQA"] = new System.Text.Json.Nodes.JsonObject{
            ["enabled"] = true,
            ["maxMissingBarsPerInstrument"] = 999,
            ["allowDuplicates"] = true,
            ["spikeZ"] = 50,
            ["repair"] = new System.Text.Json.Nodes.JsonObject{ ["forwardFillBars"] = 1, ["dropSpikes"] = false }
        };
        // Introduce benign duplicate: append a duplicate tick row to one symbol file
        var ticksDir = Path.Combine(root, "tests","fixtures","backtest_m0");
        var eurusdPath = Path.Combine(ticksDir, "ticks_EURUSD.csv");
        var dupLine = File.ReadAllLines(eurusdPath).Skip(1).First();
        var tmpRoot = Path.Combine(Path.GetTempPath(), "dq-active-tolerated"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmpRoot);
        // Copy fixture directory
        foreach (var f in Directory.GetFiles(ticksDir, "ticks_*.csv")) File.Copy(f, Path.Combine(tmpRoot, Path.GetFileName(f)));
        File.AppendAllText(Path.Combine(tmpRoot, "ticks_EURUSD.csv"), Environment.NewLine + dupLine);
        // Update config data ticks root
        var dataNode = node["data"]!.AsObject();
        var ticksObj = dataNode["ticks"]!.AsObject();
        foreach (var kv in ticksObj.ToList())
            ticksObj[kv.Key] = Path.Combine(tmpRoot, Path.GetFileName(kv.Value!.GetValue<string>())).Replace('\\','/');
        dataNode["instrumentsFile"] = Path.Combine(root, "tests","fixtures","backtest_m0","instruments.csv").Replace('\\','/');
        var cfgPath = Path.Combine(tmpRoot, "config.json"); File.WriteAllText(cfgPath, node.ToJsonString());
        var runId = RunSim(root, cfgPath);
        var (eventsPath, _) = LocateJournal(root, runId);
        var lines = File.ReadAllLines(eventsPath);
        var summary = lines.First(l=>l.Contains(",DATA_QA_SUMMARY_V1,"));
        Assert.Contains("\"passed\":true", summary);
        Assert.Contains("\"aborted\":false", summary);
        // tolerated_count > 0
    var tolCountMatch = System.Text.RegularExpressions.Regex.Match(summary, "\\\"tolerated_count\\\":(\\d+)");
    Assert.True(tolCountMatch.Success, "tolerated_count missing");
    Assert.True(int.Parse(tolCountMatch.Groups[1].Value) > 0, "Expected tolerated_count > 0");
    }

    [Fact]
    public void Active_HardFail_Aborts()
    {
        var root = FindSolutionRoot();
        var cfgPath = Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.json");
        var tmpDir = Path.Combine(Path.GetTempPath(), "dq-active-fail"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmpDir);
        var origRoot = Path.Combine(root, "tests","fixtures","backtest_m0");
        // Copy tick files but remove a block to induce missing bars
        var fixtureTicks = Path.Combine(tmpDir, "ticks"); Directory.CreateDirectory(fixtureTicks);
        foreach (var f in Directory.GetFiles(origRoot, "ticks_*.csv"))
        {
            var all = File.ReadAllLines(f).ToList();
            all = all.Where(l=>!l.Contains("2025-01-02T00:30:")) .ToList();
            File.WriteAllLines(Path.Combine(fixtureTicks, Path.GetFileName(f)), all);
        }
        var cfgJsonOrig = File.ReadAllText(cfgPath);
        using var doc = System.Text.Json.JsonDocument.Parse(cfgJsonOrig);
        var cfgObj = new Dictionary<string,object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.NameEquals("data"))
            {
                var dataNode = prop.Value;
                var ticks = new Dictionary<string,string>();
                foreach (var tk in dataNode.GetProperty("ticks").EnumerateObject())
                {
                    ticks[tk.Name] = Path.Combine(fixtureTicks, tk.Value.GetString()!.Split('/').Last()).Replace('\\','/');
                }
                var dataObj = new Dictionary<string,object?>{
                    ["type"] = dataNode.GetProperty("type").GetString(),
                    ["root"] = fixtureTicks.Replace('\\','/'),
                    ["instrumentsFile"] = Path.Combine(origRoot, "instruments.csv").Replace('\\','/'),
                    ["ticks"] = ticks,
                    ["window"] = new Dictionary<string,string>{ ["fromUtc"] = dataNode.GetProperty("window").GetProperty("fromUtc").GetString()!, ["toUtc"] = dataNode.GetProperty("window").GetProperty("toUtc").GetString()! }
                };
                cfgObj[prop.Name] = dataObj;
            }
            else cfgObj[prop.Name] = System.Text.Json.JsonSerializer.Deserialize<object?>(prop.Value.GetRawText());
        }
        cfgObj["featureFlags"] = new Dictionary<string,object?>{ ["dataQa"] = "active" };
        cfgObj["dataQA"] = new Dictionary<string,object?>{
            ["enabled"] = true,
            ["maxMissingBarsPerInstrument"] = 0,
            ["allowDuplicates"] = false,
            ["spikeZ"] = 5,
            ["repair"] = new Dictionary<string,object?>{ ["forwardFillBars"] = 0, ["dropSpikes"] = true }
        };
        var finalCfg = System.Text.Json.JsonSerializer.Serialize(cfgObj, new System.Text.Json.JsonSerializerOptions{WriteIndented=false});
        var modCfg = Path.Combine(tmpDir, "config.json"); File.WriteAllText(modCfg, finalCfg);
        var runId = RunSim(root, modCfg);
        var (eventsPath, tradesPath) = LocateJournal(root, runId);
        var lines = File.ReadAllLines(eventsPath);
        Assert.Contains(lines, l=>l.Contains("DATA_QA_ABORT_V1"));
        // Ensure no BAR / trades file absent or zero size after abort
        bool anyBarAfterAbort = false; bool abortSeen=false;
        foreach (var l in lines)
        {
            if (l.Contains(",DATA_QA_ABORT_V1,")) { abortSeen=true; continue; }
            if (abortSeen && l.Contains(",BAR_V1,")) { anyBarAfterAbort=true; break; }
        }
        Assert.False(anyBarAfterAbort, "BAR_V1 emitted after abort");
        // trades should not exist or be empty
        if (File.Exists(tradesPath))
        {
            var tSize = new FileInfo(tradesPath).Length;
            Assert.True(tSize==0 || tSize<50, "Trades file unexpectedly populated after abort");
        }
    }

    [Fact]
    public void Active_HardFail_AbortTailDeterministic()
    {
        var root = FindSolutionRoot();
        string MakeFailingConfigCopy()
        {
            var baseCfg = Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.json");
            var tmpDir = Path.Combine(Path.GetTempPath(), "dq-abort-tail"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmpDir);
            var origRoot = Path.Combine(root, "tests","fixtures","backtest_m0");
            var fixtureTicks = Path.Combine(tmpDir, "ticks"); Directory.CreateDirectory(fixtureTicks);
            foreach (var f in Directory.GetFiles(origRoot, "ticks_*.csv"))
            {
                // remove all ticks for a given minute to force missing bars
                var all = File.ReadAllLines(f).Where(l=>!l.Contains("2025-01-02T00:30:")).ToList();
                File.WriteAllLines(Path.Combine(fixtureTicks, Path.GetFileName(f)), all);
            }
            var cfgJsonOrig = File.ReadAllText(baseCfg);
            using var doc = System.Text.Json.JsonDocument.Parse(cfgJsonOrig);
            var cfgObj = new Dictionary<string,object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("data"))
                {
                    var dataNode = prop.Value;
                    var ticks = new Dictionary<string,string>();
                    foreach (var tk in dataNode.GetProperty("ticks").EnumerateObject())
                        ticks[tk.Name] = Path.Combine(fixtureTicks, tk.Value.GetString()!.Split('/').Last()).Replace('\\','/');
                    cfgObj[prop.Name] = new Dictionary<string,object?>{
                        ["type"] = dataNode.GetProperty("type").GetString(),
                        ["root"] = fixtureTicks.Replace('\\','/'),
                        ["instrumentsFile"] = Path.Combine(origRoot, "instruments.csv").Replace('\\','/'),
                        ["ticks"] = ticks,
                        ["window"] = new Dictionary<string,string>{ ["fromUtc"] = dataNode.GetProperty("window").GetProperty("fromUtc").GetString()!, ["toUtc"] = dataNode.GetProperty("window").GetProperty("toUtc").GetString()! }
                    };
                }
                else cfgObj[prop.Name] = System.Text.Json.JsonSerializer.Deserialize<object?>(prop.Value.GetRawText());
            }
            cfgObj["featureFlags"] = new Dictionary<string,object?>{ ["dataQa"] = "active" };
            cfgObj["dataQA"] = new Dictionary<string,object?>{
                ["enabled"] = true,
                ["maxMissingBarsPerInstrument"] = 0,
                ["allowDuplicates"] = false,
                ["spikeZ"] = 5,
                ["repair"] = new Dictionary<string,object?>{ ["forwardFillBars"] = 0, ["dropSpikes"] = true }
            };
            var finalCfg = System.Text.Json.JsonSerializer.Serialize(cfgObj, new System.Text.Json.JsonSerializerOptions{WriteIndented=false});
            var path = Path.Combine(tmpDir, "config.json"); File.WriteAllText(path, finalCfg); return path;
        }

        string HashTail(string eventsPath)
        {
            var lines = File.ReadAllLines(eventsPath);
            var firstIdx = Array.FindIndex(lines, l=>l.Contains(",DATA_QA_"));
            Assert.True(firstIdx >= 0, "No DATA_QA_ events found in failing run");
            var tail = string.Join('\n', lines.Skip(firstIdx));
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(tail));
            return BitConverter.ToString(bytes).Replace("-", "");
        }

        string RunFailAndHash()
        {
            var cfg = MakeFailingConfigCopy();
            var runId = RunSim(root, cfg);
            var (eventsPath, _) = LocateJournal(root, runId);
            return HashTail(eventsPath);
        }

        var h1 = RunFailAndHash();
        var h2 = RunFailAndHash();
        Assert.Equal(h1, h2);
    }
}