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

    /// <summary>
    /// Missing bars under strict tolerance (maxMissingBarsPerInstrument=0 in active mode) now trigger an abort.
    /// We assert:
    ///  - DATA_QA_ISSUE_V1 entries with kind=missing_bar exist
    ///  - DATA_QA_SUMMARY_V1 has passed=false and aborted=true
    ///  - DATA_QA_ABORT_V1 emitted
    ///  - No BAR_V1 events after the abort marker
    /// </summary>
    [Fact]
    public void MissingBars_HardFail_Aborts()
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
            // Remove a larger continuous time window (e.g., full 00:30 to 00:40 block) to guarantee abort under strict tolerance.
            var removed = all.Where(l => l.Contains("2025-01-02T00:30:") || l.Contains("2025-01-02T00:31:") || l.Contains("2025-01-02T00:32:") || l.Contains("2025-01-02T00:33:") || l.Contains("2025-01-02T00:34:") || l.Contains("2025-01-02T00:35:") || l.Contains("2025-01-02T00:36:") || l.Contains("2025-01-02T00:37:") || l.Contains("2025-01-02T00:38:") || l.Contains("2025-01-02T00:39:")).ToList();
            all = all.Where(l => !removed.Contains(l)).ToList();
            File.WriteAllLines(dest, all);
            Assert.True(removed.Count > 5, "Expected to remove multiple consecutive minute blocks to force abort");
        }
        // Apply severe gap to two instruments to ensure failure severity
        CopyAndGap("ticks_EURUSD.csv");
        CopyAndGap("ticks_USDJPY.csv");
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
        // Activate Data QA so abort event is expected
    cfgObj["featureFlags"] = new Dictionary<string,object?>{ ["dataQa"] = "active", ["riskProbe"] = "disabled" };
        var finalCfg = System.Text.Json.JsonSerializer.Serialize(cfgObj, new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
        var modCfg = Path.Combine(tmpDir, "config.json");
        File.WriteAllText(modCfg, finalCfg);
    var runId = RunSim(root, modCfg);
    var journalBase = Path.Combine(root, "journals","M0",$"M0-RUN-{runId}");
        var eventsFile = Path.Combine(journalBase, "events.csv");
        Assert.True(File.Exists(eventsFile));
        var lines = File.ReadAllLines(eventsFile);
        // Expect at least one missing_bar issue
        Assert.Contains(lines, l => l.Contains(",DATA_QA_ISSUE_V1,") && l.Contains("missing_bar"));
        var summaryLine2 = lines.First(l => l.Contains(",DATA_QA_SUMMARY_V1,"));
        var parts2 = summaryLine2.Split(',',4);
        var payload2 = parts2[3].Trim().Trim('"').Replace("\"\"","\"");
        using (var doc2 = System.Text.Json.JsonDocument.Parse(payload2))
        {
            Assert.True(doc2.RootElement.TryGetProperty("passed", out var p) && p.ValueKind==System.Text.Json.JsonValueKind.False, "Expected passed=false in summary");
            Assert.True(doc2.RootElement.TryGetProperty("aborted", out var ab) && ab.ValueKind==System.Text.Json.JsonValueKind.True, "Expected aborted=true");
        }
        Assert.Contains(lines, l => l.Contains(",DATA_QA_ABORT_V1,"));
        // Ensure no BAR_V1 after abort
        bool abortSeen=false; bool barAfter=false;
        foreach (var l in lines)
        {
            if (l.Contains(",DATA_QA_ABORT_V1,")) { abortSeen = true; continue; }
            if (abortSeen && l.Contains(",BAR_V1,")) { barAfter=true; break; }
        }
        Assert.False(barAfter, "BAR_V1 emitted after abort");
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
        var args2 = $"exec \"{simDll}\" --config \"{configPath}\" --verbosity quiet --run-id {runId}";
        var psi2 = new ProcessStartInfo("dotnet", args2) { WorkingDirectory = root, RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false };
        var p2 = Process.Start(psi2)!;
        var stdout2 = new StringBuilder(); var stderr2 = new StringBuilder();
        p2.OutputDataReceived += (_,e)=> { if (e.Data!=null) stdout2.AppendLine(e.Data); }; p2.BeginOutputReadLine();
        p2.ErrorDataReceived += (_,e)=> { if (e.Data!=null) stderr2.AppendLine(e.Data); }; p2.BeginErrorReadLine();
        if (!p2.WaitForExit(60000)) { try { p2.Kill(); } catch { } Assert.Fail($"Sim timeout. CMD=dotnet {args2}\nCWD={root}\nSTDOUT:\n{stdout2}\nSTDERR:\n{stderr2}"); }
        if (p2.ExitCode != 0)
        {
            var journalBase = Path.Combine(root, "journals","M0",$"M0-RUN-{runId}");
            Assert.Fail($"Sim non-zero exit. Code={p2.ExitCode}\nCMD=dotnet {args2}\nCWD={root}\nJOURNAL_DIR={journalBase}\nSTDOUT:\n{stdout2}\nSTDERR:\n{stderr2}");
        }
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
            ["maxMissingBarsPerInstrument"] = 999, // tolerate missing bars
            ["allowDuplicates"] = true,             // duplicate issues will be dropped -> increase tolerated_count
            ["spikeZ"] = 50,                        // spikes tolerated
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
        var parts = summary.Split(',',4); Assert.True(parts.Length==4, "summary CSV malformed");
        var payloadRaw = parts[3];
    var json = payloadRaw.Trim().Trim('"').Replace("\"\"", "\"");
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
        {
            var rootEl = doc.RootElement;
            Assert.True(rootEl.TryGetProperty("passed", out var pEl) && pEl.ValueKind==System.Text.Json.JsonValueKind.True, "Expected passed=true");
            Assert.True(rootEl.TryGetProperty("aborted", out var aEl) && aEl.ValueKind==System.Text.Json.JsonValueKind.False, "Expected aborted=false");
            Assert.True(rootEl.TryGetProperty("tolerance_profile_hash", out var hEl) && hEl.GetString()!.Length==64, "Missing tolerance_profile_hash 64 hex");
        }
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
        // Introduce tolerated missing bars: remove all ticks for a minute block (00:30) from one symbol.
        var ticksDir = Path.Combine(root, "tests","fixtures","backtest_m0");
        var tmpRoot = Path.Combine(Path.GetTempPath(), "dq-active-tolerated"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmpRoot);
    foreach (var f in Directory.GetFiles(ticksDir, "ticks_*.csv").OrderBy(p=>p, StringComparer.Ordinal))
        {
            var linesF = File.ReadAllLines(f).ToList();
            if (Path.GetFileName(f)=="ticks_EURUSD.csv")
            {
                var removed = linesF.RemoveAll(l => l.Contains("2025-01-02T00:30:"));
                Assert.True(removed > 0, "Expected to remove at least one tick to create missing bar");
            }
            File.WriteAllLines(Path.Combine(tmpRoot, Path.GetFileName(f)), linesF);
        }
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
        var parts2 = summary.Split(',',4); Assert.True(parts2.Length==4, "summary CSV malformed");
    var json2 = parts2[3].Trim().Trim('"').Replace("\"\"", "\"");
        using (var doc = System.Text.Json.JsonDocument.Parse(json2))
        {
            var rootEl = doc.RootElement;
            Assert.True(rootEl.TryGetProperty("passed", out var pEl) && pEl.ValueKind==System.Text.Json.JsonValueKind.True, "Expected passed=true");
            Assert.True(rootEl.TryGetProperty("aborted", out var aEl) && aEl.ValueKind==System.Text.Json.JsonValueKind.False, "Expected aborted=false");
            Assert.True(rootEl.TryGetProperty("tolerated_count", out var tEl) && tEl.GetInt32() > 0, "Expected tolerated_count > 0");
        }
    }

    [Fact]
    public void DataQa_Active_Tolerates_K_MissingBars_NoAbort()
    {
        var root = FindSolutionRoot();
        var baseCfgPath = Path.Combine(root, "tests","fixtures","backtest_m0","config.backtest-m0.json");
        Assert.True(File.Exists(baseCfgPath));
        var cfgNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(baseCfgPath))!.AsObject();
        cfgNode["featureFlags"] = new System.Text.Json.Nodes.JsonObject{ ["dataQa"] = "active" };
        cfgNode["dataQA"] = new System.Text.Json.Nodes.JsonObject{
            ["enabled"] = true,
            ["maxMissingBarsPerInstrument"] = 1,
            ["allowDuplicates"] = false,
            ["spikeZ"] = 5,
            ["repair"] = new System.Text.Json.Nodes.JsonObject{ ["forwardFillBars"] = 0, ["dropSpikes"] = true }
        };

        // Create a temp ticks root with exactly one missing minute for EURUSD
        var tmpRoot = Path.Combine(Path.GetTempPath(), "dq-k1-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(tmpRoot);
        var origRoot = Path.Combine(root, "tests","fixtures","backtest_m0");
        foreach (var f in Directory.GetFiles(origRoot, "ticks_*.csv").OrderBy(p=>p, StringComparer.Ordinal))
        {
            var linesF = File.ReadAllLines(f).ToList();
            if (Path.GetFileName(f) == "ticks_EURUSD.csv")
            {
                var removed = linesF.RemoveAll(l => l.Contains("2025-01-02T00:30:"));
                Assert.True(removed > 0, "Expected to remove at least one tick to create one missing bar for EURUSD");
            }
            File.WriteAllLines(Path.Combine(tmpRoot, Path.GetFileName(f)), linesF);
        }
        // Point config to temp ticks
        var dataObj = cfgNode["data"]!.AsObject();
        var ticks = dataObj["ticks"]!.AsObject();
        foreach (var kv in ticks.ToList())
            ticks[kv.Key] = Path.Combine(tmpRoot, Path.GetFileName(kv.Value!.GetValue<string>())).Replace('\\','/');
        dataObj["instrumentsFile"] = Path.Combine(origRoot, "instruments.csv").Replace('\\','/');

        var tmpCfg = Path.Combine(tmpRoot, "config.json");
        File.WriteAllText(tmpCfg, cfgNode.ToJsonString());
        var runId = RunSim(root, tmpCfg);
        var (eventsPath, _) = LocateJournal(root, runId);
        var lines = File.ReadAllLines(eventsPath);
        var summary = lines.First(l=>l.Contains(",DATA_QA_SUMMARY_V1,"));
        var payload = summary.Split(',',4)[3].Trim().Trim('"').Replace("\"\"","\"");
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        var el = doc.RootElement;
        Assert.True(el.TryGetProperty("passed", out var pEl) && pEl.ValueKind==System.Text.Json.JsonValueKind.True, "Expected passed=true with K=1 tolerance");
        Assert.True(el.TryGetProperty("aborted", out var aEl) && aEl.ValueKind==System.Text.Json.JsonValueKind.False, "Expected aborted=false with K=1 tolerance");
        Assert.DoesNotContain(lines, l=>l.Contains(",DATA_QA_ABORT_V1,"));
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
    foreach (var f in Directory.GetFiles(origRoot, "ticks_*.csv").OrderBy(p=>p, StringComparer.Ordinal))
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
            cfgObj["featureFlags"] = new Dictionary<string,object?>{ ["dataQa"] = "active", ["riskProbe"] = "disabled" };
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
                var sb = new System.Text.StringBuilder();
                foreach (var raw in lines.Skip(firstIdx))
                {
                    var parts = raw.Split(',',4);
                    if (parts.Length != 4) continue;
                    var ts = parts[1];
                    var evt = parts[2];
                    var payloadRaw = parts[3].Trim().Trim('"').Replace("\"\"","\"");
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(payloadRaw);
                        var root = doc.RootElement;
                        switch (evt)
                        {
                            case "DATA_QA_BEGIN_V1":
                                sb.Append("BEGIN|").Append(ts).Append('\n');
                                break;
                            case "DATA_QA_ISSUE_V1":
                                var symbol = root.TryGetProperty("symbol", out var symEl) ? symEl.GetString() : "";
                                var kind = root.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : "";
                                var its = root.TryGetProperty("ts", out var tsEl) ? tsEl.GetDateTime().ToString("O") : ts;
                                sb.Append("ISSUE|").Append(symbol).Append('|').Append(kind).Append('|').Append(its).Append('\n');
                                break;
                            case "DATA_QA_SUMMARY_V1":
                                var issues = root.TryGetProperty("issues", out var issEl) ? issEl.GetInt32() : -1;
                                var repaired = root.TryGetProperty("repaired", out var repEl) ? repEl.GetInt32() : -1;
                                var passed = root.TryGetProperty("passed", out var passEl) && passEl.ValueKind==System.Text.Json.JsonValueKind.True;
                                var aborted = root.TryGetProperty("aborted", out var abEl) && abEl.ValueKind==System.Text.Json.JsonValueKind.True;
                                sb.Append("SUMMARY|").Append(issues).Append('|').Append(repaired).Append('|').Append(passed?1:0).Append('|').Append(aborted?1:0).Append('\n');
                                break;
                            case "DATA_QA_ABORT_V1":
                                var reason = root.TryGetProperty("reason", out var rEl) ? rEl.GetString() : "";
                                var emitted = root.TryGetProperty("issues_emitted", out var ieEl) ? ieEl.GetInt32() : -1;
                                var tolerated = root.TryGetProperty("tolerated", out var tolEl) ? tolEl.GetInt32() : -1;
                                sb.Append("ABORT|").Append(reason).Append('|').Append(emitted).Append('|').Append(tolerated).Append('\n');
                                break;
                            default:
                                // ignore other events
                                break;
                        }
                    }
                    catch { /* ignore parse errors in test canonicalization */ }
                }
                var joined = sb.ToString();
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(joined));
            return BitConverter.ToString(bytes).Replace("-", "");
        }

        static string NormalizeDataVersion(string line)
        {
            // Replace data_version values (CSV meta style or JSON field) with constant token to remove config path variability
            // Patterns: data_version=HEX or "data_version":"HEX"
            int idx = line.IndexOf("data_version=", StringComparison.Ordinal);
            if (idx >= 0)
            {
                int start = idx + "data_version=".Length;
                int len = 0;
                while (start+len < line.Length && IsHex(line[start+len])) len++;
                if (len >= 32) line = line.Substring(0, start) + "<DV>" + line.Substring(start+len);
            }
            // JSON style
            idx = line.IndexOf("\"data_version\":\"", StringComparison.Ordinal);
            if (idx >= 0)
            {
                int start = idx + "\"data_version\":\"".Length;
                int len = 0;
                while (start+len < line.Length && IsHex(line[start+len])) len++;
                if (len >= 32) line = line.Substring(0, start) + "<DV>" + line.Substring(start+len);
            }
            return line;
        }

        static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');

        static string NormalizeToleranceProfile(string line)
        {
            var marker = "\"tolerance_profile_hash\":\"";
            int idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                int start = idx + marker.Length;
                int len = 0;
                while (start+len < line.Length && IsHex(line[start+len])) len++;
                if (len >= 32)
                {
                    line = line.Substring(0, start) + "<TPH>" + line.Substring(start+len);
                }
            }
            return line;
        }

        static string NormalizeConfigHash(string line)
        {
            var marker = "\"config_hash\":\"";
            int idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                int start = idx + marker.Length;
                int len = 0;
                while (start+len < line.Length && IsHex(line[start+len])) len++;
                if (len >= 32)
                {
                    line = line.Substring(0, start) + "<CH>" + line.Substring(start+len);
                }
            }
            return line;
        }

        List<string> CanonicalTail(string eventsPath)
        {
            var lines = File.ReadAllLines(eventsPath);
            var firstIdx = Array.FindIndex(lines, l=>l.Contains(",DATA_QA_"));
            if (firstIdx < 0) return new List<string>();
            var list = new List<string>();
            foreach (var raw in lines.Skip(firstIdx))
            {
                var l = NormalizeDataVersion(raw);
                l = NormalizeToleranceProfile(l);
                l = NormalizeConfigHash(l);
                var parts = l.Split(',',4);
                if (parts.Length==4) list.Add(parts[1]+"|"+parts[2]+"|"+parts[3]); else list.Add(l);
            }
            return list;
        }

        string cfg1 = MakeFailingConfigCopy();
        string run1 = RunSim(root, cfg1);
        var (events1, _) = LocateJournal(root, run1);
        string cfg2 = MakeFailingConfigCopy();
        string run2 = RunSim(root, cfg2);
        var (events2, _) = LocateJournal(root, run2);
        var h1 = HashTail(events1);
        var h2 = HashTail(events2);
        if (h1 != h2)
        {
            var t1 = CanonicalTail(events1);
            var t2 = CanonicalTail(events2);
            Console.WriteLine($"DQ_ABORT_TAIL_MISMATCH hash1={h1} hash2={h2} lines1={t1.Count} lines2={t2.Count}");
            int max = Math.Max(t1.Count, t2.Count);
            for (int i=0;i<max;i++)
            {
                var a = i < t1.Count ? t1[i] : "<EOF>";
                var b = i < t2.Count ? t2[i] : "<EOF>";
                if (!string.Equals(a,b,StringComparison.Ordinal))
                {
                    Console.WriteLine($"FIRST_DIFF line={i+1}\nA:{a}\nB:{b}");
                    break;
                }
            }
        }
        Assert.Equal(h1, h2);
    }

    [Fact(Skip="No current scenario sets aborted=true for missing bars; placeholder for future hard abort trigger test")]
    public void HardAbort_Trigger_Placeholder() { }
}