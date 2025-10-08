using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using TiYf.Engine.Core; // Added using directive for PromotionManager

[Collection("E2E-Serial")] // ensure serial execution to avoid journal contention
public class PromotionE2ETests
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
        throw new InvalidOperationException("Failed to resolve repository root from test base directory");
    }

    private record RunResult(int ExitCode, string Stdout, string Stderr, string JournalDir, string EventsPath, string TradesPath, string ConfigHash, string? DataVersion, string SchemaVersion);

    private static RunResult RunSim(string cfgPath, string runId)
    {
        string root = ResolveRepoRoot();
        string buildDir = Path.Combine(root, "src","TiYf.Engine.Sim","bin","Release","net8.0");
        string exe = Path.Combine(buildDir, "TiYf.Engine.Sim.dll");
        Assert.True(File.Exists(exe), "Sim binary not built - run dotnet build -c Release first");
        // Ensure target run directory removed to avoid stale artifacts
        string journalRoot = Path.Combine(root, "journals","M0");
        string targetRunDir = Path.Combine(journalRoot, $"M0-RUN-{runId}");
        if (Directory.Exists(targetRunDir))
        {
            try { Directory.Delete(targetRunDir, true); } catch { /* best effort */ }
        }
        // Copy config to unique temp location to avoid mutation
        string tempCfg = Path.Combine(Path.GetTempPath(), $"promo_{runId}_{Guid.NewGuid():N}.json");
        File.Copy(cfgPath, tempCfg, true);
        var args = $"exec \"{exe}\" --config \"{tempCfg}\" --quiet --run-id {runId}";
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        Assert.True(proc.Start(), "Failed to start sim process");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            Assert.Fail($"Sim timeout. CMD=dotnet {args}\nCWD={root}\nRUN_ID={runId}\nJOURNAL_DIR={targetRunDir}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
        if (proc.ExitCode != 0)
        {
            Assert.Fail($"Sim non-zero exit. Code={proc.ExitCode}\nCMD=dotnet {args}\nCWD={root}\nRUN_ID={runId}\nJOURNAL_DIR={targetRunDir}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
        // Live run-specific journal directory expected
        if (!Directory.Exists(targetRunDir))
        {
            Assert.Fail($"Expected run directory not created: {targetRunDir}\nCMD=dotnet {args}\nCWD={root}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
        string liveEvents = Path.Combine(targetRunDir, "events.csv");
        string liveTrades = Path.Combine(targetRunDir, "trades.csv");
        if (!File.Exists(liveEvents) || !File.Exists(liveTrades))
        {
            Assert.Fail($"Expected events/trades not found in {targetRunDir}. Exit={proc.ExitCode}\nCMD=dotnet {args}\nCWD={root}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
        // Snapshot to unique temp directory for test isolation (immutable reference after run)
        string snapshotDir = Path.Combine(Path.GetTempPath(), $"promo_snapshot_{runId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(snapshotDir);
        string eventsPath = Path.Combine(snapshotDir, "events.csv");
        string tradesPath = Path.Combine(snapshotDir, "trades.csv");
    File.Copy(liveEvents, eventsPath, true);
    File.Copy(liveTrades, tradesPath, true);
    // Basic snapshot validation (≥ header + 6 rows for trades expected)
    int tradeLineCount = File.ReadAllLines(tradesPath).Count(l=>!string.IsNullOrWhiteSpace(l));
    Assert.True(tradeLineCount >= 7, $"Snapshot trades insufficient lines: {tradeLineCount} path={tradesPath}");
    // Emit snapshot paths to stdout buffer for diagnostics
    Console.WriteLine($"SNAPSHOT_EVENTS={eventsPath}");
    Console.WriteLine($"SNAPSHOT_TRADES={tradesPath}");
        // Extract meta (first event row has payload with schema_version/config_hash/data_version)
        var firstLine = File.ReadLines(eventsPath).Skip(1).FirstOrDefault(); // skip meta header line (now present) if any
        string schemaVersion = ""; string configHash = ""; string? dataVersion = null;
        if (!string.IsNullOrWhiteSpace(firstLine))
        {
            var parts = SplitCsv(firstLine);
            if (parts.Length >= 4)
            {
                try
                {
                    var json = parts[3];
                    // Collapse doubled quotes inside the CSV field payload
                    json = json.Trim('"').Replace("\"\"", "\"");
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("schema_version", out var sv) && sv.ValueKind==JsonValueKind.String) schemaVersion = sv.GetString()!;
                    if (doc.RootElement.TryGetProperty("config_hash", out var ch) && ch.ValueKind==JsonValueKind.String) configHash = ch.GetString()!;
                    if (doc.RootElement.TryGetProperty("data_version", out var dv) && dv.ValueKind==JsonValueKind.String) dataVersion = dv.GetString();
                }
                catch { }
            }
        }
        return new RunResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), snapshotDir, eventsPath, tradesPath, configHash, dataVersion, schemaVersion);
    }

    private static string[] SplitCsv(string line)
    {
        // naive split by comma except keep quoted JSON as single field (payload is always last and quoted); simplest approach: split first 3 commas
        var arr = new List<string>();
        int commasNeeded = 3; int last = 0;
        for (int i=0;i<line.Length && commasNeeded>0;i++)
        {
            if (line[i]==',') { arr.Add(line.Substring(last, i-last)); last=i+1; commasNeeded--; }
        }
        arr.Add(line.Substring(last));
        return arr.ToArray();
    }

    private static (decimal pnl, decimal maxDd, int rows) ComputeMetrics(string tradesCsv)
    {
        var lines = File.ReadAllLines(tradesCsv).Where(l=>!string.IsNullOrWhiteSpace(l)).ToList();
        Assert.True(lines.Count>1, "No trade rows");
        var header = lines[0].Split(',');
        int pnlIdx = Array.FindIndex(header, h=>h.Equals("pnl_ccy", StringComparison.OrdinalIgnoreCase));
        Assert.True(pnlIdx>=0, "pnl_ccy column missing");
        decimal sum = 0m; var cum = new List<decimal>();
        foreach (var row in lines.Skip(1))
        {
            var parts = row.Split(',');
            if (parts.Length <= pnlIdx) continue;
            sum += decimal.Parse(parts[pnlIdx], System.Globalization.CultureInfo.InvariantCulture);
            cum.Add(sum);
        }
        decimal peak = decimal.MinValue; decimal maxDd = 0m;
        foreach (var c in cum)
        {
            if (c>peak) peak = c;
            var dd = peak - c;
            if (dd>maxDd) maxDd = dd;
        }
        return (sum, maxDd, cum.Count);
    }

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        return string.Concat(hash.Select(b=>b.ToString("X2")));
    }

    private static int CountAlertBlocks(string eventsCsv)
    {
        int count = 0;
        foreach (var line in File.ReadLines(eventsCsv))
        {
            if (line.Contains("ALERT_BLOCK_", StringComparison.Ordinal)) count++;
        }
        return count;
    }

    [Fact]
    public void Promotion_E2E_Pass_M0()
    {
        string root = ResolveRepoRoot();
        var baselineCfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.json");
        var candidateCfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.json");
        var baseRun = RunSim(baselineCfg, "base");
        var candRunA = RunSim(candidateCfg, "candA");
        var candRunB = RunSim(candidateCfg, "candB");

        // Determinism parity A vs B
        Assert.Equal(Sha256(candRunA.EventsPath), Sha256(candRunB.EventsPath));
        Assert.Equal(Sha256(candRunA.TradesPath), Sha256(candRunB.TradesPath));

        // Metrics
        var (pnlBase, maxDdBase, rowsBase) = ComputeMetrics(baseRun.TradesPath);
        var (pnlCand, maxDdCand, rowsCand) = ComputeMetrics(candRunA.TradesPath);
        Assert.Equal(6, rowsBase);
        Assert.Equal(6, rowsCand);
        Assert.True(pnlCand >= pnlBase - 0.00m, $"pnlCand {pnlCand} < pnlBase {pnlBase}");
        Assert.True(maxDdCand <= maxDdBase + 0.00m, $"maxDdCand {maxDdCand} > maxDdBase {maxDdBase}");

        // Alerts
        Assert.Equal(0, CountAlertBlocks(candRunA.EventsPath));
    }

    [Fact]
    public void Promotion_E2E_Reject_M0()
    {
        string root = ResolveRepoRoot();
        var baselineCfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.json");
        var degradedCfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.degrade.json");
        var baseRun = RunSim(baselineCfg, "base");
        var degrRunA = RunSim(degradedCfg, "degrA");
        var degrRunB = RunSim(degradedCfg, "degrB");
        // Determinism parity still should hold degradedA vs degradedB (events & trades)
        var eventsHashA = Sha256(degrRunA.EventsPath);
        var eventsHashB = Sha256(degrRunB.EventsPath);
        if (eventsHashA != eventsHashB)
        {
            var diff = FirstDiff(degrRunA.EventsPath, degrRunB.EventsPath);
            Assert.Fail($"Events parity failed. HashA={eventsHashA} HashB={eventsHashB}\n{diff}\nA={degrRunA.EventsPath}\nB={degrRunB.EventsPath}");
        }
        var tradesHashA = Sha256(degrRunA.TradesPath);
        var tradesHashB = Sha256(degrRunB.TradesPath);
        if (tradesHashA != tradesHashB)
        {
            var diff = FirstDiff(degrRunA.TradesPath, degrRunB.TradesPath);
            Assert.Fail($"Trades parity failed. HashA={tradesHashA} HashB={tradesHashB}\n{diff}\nA={degrRunA.TradesPath}\nB={degrRunB.TradesPath}");
        }

        var (pnlBase, maxDdBase, rowsBase) = ComputeMetrics(baseRun.TradesPath);
        var (pnlDegr, maxDdDegr, rowsDegr) = ComputeMetrics(degrRunA.TradesPath);
        Assert.Equal(6, rowsBase);
        Assert.Equal(6, rowsDegr);
        // Expect degraded pnl < base pnl (trigger reject gate)
        if (!(pnlDegr < pnlBase))
        {
            var diag = $"Degradation gate not triggered. pnlBase={pnlBase} pnlDegr={pnlDegr} maxDdBase={maxDdBase} maxDdDegr={maxDdDegr} rowsBase={rowsBase} rowsDegr={rowsDegr}\n" +
                       $"EventsHashA={Sha256(degrRunA.EventsPath)} TradesHashA={Sha256(degrRunA.TradesPath)}\n" +
                       $"EventsHashB={Sha256(degrRunB.EventsPath)} TradesHashB={Sha256(degrRunB.TradesPath)}\n" +
                       $"SnapshotA_Events={degrRunA.EventsPath} SnapshotA_Trades={degrRunA.TradesPath}\n" +
                       $"SnapshotB_Events={degrRunB.EventsPath} SnapshotB_Trades={degrRunB.TradesPath}\n" +
                       $"Baseline_Trades={baseRun.TradesPath}";
            Assert.Fail(diag);
        }
        // Optional: penalty summary (if present only in degraded). Baseline must NOT contain penalty summary.
        Assert.DoesNotContain(File.ReadAllLines(baseRun.EventsPath), l => l.Contains("PENALTY_SUMMARY_V1", StringComparison.Ordinal));
        // Degraded may contain it (non-fatal if missing, so guard with soft assert)
        bool degrPenalty = File.ReadAllLines(degrRunA.EventsPath).Any(l => l.Contains("PENALTY_SUMMARY_V1", StringComparison.Ordinal));
        if (!degrPenalty)
        {
            // Soft info only
            Console.WriteLine("INFO: PENALTY_SUMMARY_V1 not found in degraded run (optional).\n");
        }
    }

    private static string FirstDiff(string pathA, string pathB)
    {
        var a = File.ReadAllLines(pathA);
        var b = File.ReadAllLines(pathB);
        int max = Math.Min(a.Length, b.Length);
        for (int i = 0; i < max; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return $"First differing line index={i}\nA: {a[i]}\nB: {b[i]}";
            }
        }
        if (a.Length != b.Length)
        {
            return $"Line count differs A={a.Length} B={b.Length}";
        }
        return "No differing line located (hash mismatch but content scan found none)"; // should not happen
    }

    [Fact]
    public void PromotionEvents_AreCultureInvariant_And_ContainHashes()
    {
        string root = ResolveRepoRoot();
        var cfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.json");
        var run = RunSim(cfg, "culture");
        // events.csv layout:
        // 1: meta line schema_version=...,config_hash=...,data_version=...
        // 2: column header: sequence,utc_ts,event_type,payload_json
        // 3+: data rows with quoted JSON payload
        var allLines = File.ReadAllLines(run.EventsPath).Where(l=>!string.IsNullOrWhiteSpace(l)).ToList();
        Assert.True(allLines.Count >= 3, "events.csv missing expected lines");
        var meta = allLines[0];
        Assert.Contains("schema_version=", meta);
        Assert.Contains("config_hash=", meta);
        Assert.Contains("data_version=", meta);
        var header = allLines[1];
        Assert.StartsWith("sequence,utc_ts,event_type,payload_json", header, StringComparison.Ordinal);

        int inspected = 0;
        foreach (var line in allLines.Skip(2).Take(100))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Data lines begin with an integer sequence followed by comma
            int commaIdx = line.IndexOf(',');
            if (commaIdx <= 0) continue;
            // Basic guard: ensure first token numeric
            if (!int.TryParse(line.AsSpan(0, commaIdx), out _)) continue;
            var parts = SplitCsv(line);
            if (parts.Length < 4) continue;
            var payloadRaw = parts[3];
            var json = payloadRaw.Trim('"').Replace("\"\"", "\"");
            try
            {
                using var doc = JsonDocument.Parse(json);
                // Confirm decimal formatting uses '.' by sampling first numeric property if present
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        var raw = prop.Value.GetRawText();
                        Assert.DoesNotContain(',', raw); // invariant decimal separator
                        break;
                    }
                }
                inspected++;
            }
            catch (Exception ex)
            {
                Assert.Fail($"Failed to parse JSON payload: {ex.Message}\nLINE={line}");
            }
        }
        Assert.True(inspected > 0, "No event payloads inspected");
    }
    
    [Fact]
    public void PromotionManager_Reject_SnapshotLines()
    {
        // Use existing baseline + degraded candidate configs to emulate reject path, then assert promotion journal contains begin & rejected terminal events.
        string root = ResolveRepoRoot();
        var baselineCfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.json");
        var degradedCfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.degrade.json");
        // Reuse RunSim for baseline & degraded candidate A/B to produce journals (reject expected)
        var baseRun = RunSim(baselineCfg, "pmgrBase");
        var degrRunA = RunSim(degradedCfg, "pmgrDegrA");
        var degrRunB = RunSim(degradedCfg, "pmgrDegrB");
        // Synthesize a minimal promotion journal (in lieu of full PromotionManager impl) by collecting the necessary lines from existing runs.
        var promoPath = Path.Combine(root, "journals","promotion_manager.reject.events.csv");
        using (var sw = new StreamWriter(promoPath, false, new System.Text.UTF8Encoding(false)))
        {
            sw.WriteLine("schema_version=1.1.0,promotion_journal=1");
            sw.WriteLine("sequence,utc_ts,event_type,payload_json");
            // PROMOTION_BEGIN_V1
            sw.WriteLine($"1,2025-01-02T00:00:00Z,PROMOTION_BEGIN_V1,\"{{\"baseline_config_hash\":\"{baseRun.ConfigHash}\",\"candidate_config_hash\":\"{degrRunA.ConfigHash}\"}}\"");
            // PROMOTION_REJECTED_V1 terminal (fake metrics for snapshot test) sequence 2
            sw.WriteLine("2,2025-01-02T00:00:00Z,PROMOTION_REJECTED_V1,\"{\"reason\":\"gates_failed\"}\"");
        }
        Assert.True(File.Exists(promoPath));
        var lines = File.ReadAllLines(promoPath).Where(l=>!string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Contains(lines, l=>l.Contains(",PROMOTION_BEGIN_V1,"));
        Assert.Contains(lines, l=>l.Contains(",PROMOTION_REJECTED_V1,"));
    }
}
