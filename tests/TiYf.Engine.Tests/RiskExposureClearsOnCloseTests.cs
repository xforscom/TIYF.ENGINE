using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace TiYf.Engine.Tests;

public class RiskExposureClearsOnCloseTests
{
    private string SolutionRoot => FindSolutionRoot();
    private string SimDll => Path.Combine(SolutionRoot, "src", "TiYf.Engine.Sim", "bin", "Release", "net8.0", "TiYf.Engine.Sim.dll");

    [Fact]
    public void NetExposure_Resets_ToZero_After_Closes()
    {
        // Use shadow mode to emit INFO_RISK_EVAL_V1 evaluations without blocking trades
        var baseConfig = Path.Combine(SolutionRoot, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json");
        Assert.True(File.Exists(baseConfig));

        var tmp = TempConfigWithRisk(baseConfig, "shadow");
        var run = RunSim(tmp);
        var events = File.ReadAllLines(run);

        // Extract all INFO_RISK_EVAL_V1 JSON payloads
        var evals = events
            .Where(l => l.Contains(",INFO_RISK_EVAL_V1,"))
            .Select(l =>
            {
                var cols = SplitCsvQuoted(l);
                var payload = cols.Last();
                return JsonDocument.Parse(payload).RootElement;
            })
            .ToList();

        Assert.NotEmpty(evals);

        // Ensure we saw some non-zero exposure during the run
        bool anyNonZero = evals.Any(e => Math.Abs(e.GetProperty("net_exposure").GetDecimal()) > 0);
        Assert.True(anyNonZero);

        // Final evaluation should have zero net exposure once all closes have been applied
        var last = evals.Last();
        Assert.Equal(0m, last.GetProperty("net_exposure").GetDecimal());
    }

    private static string UnwrapCsv(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            var inner = raw.Substring(1, raw.Length - 2);
            return inner.Replace("\"\"", "\"");
        }
        return raw;
    }

    private static List<string> SplitCsvQuoted(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    // Escaped quote
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else { inQuotes = false; }
                }
                else { sb.Append(c); }
            }
            else
            {
                if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else if (c == '"') { inQuotes = true; }
                else { sb.Append(c); }
            }
        }
        result.Add(sb.ToString());
        return result;
    }

    private static (string events, string trades) RunSimWithOutputs(string cfg)
    {
        var (root, eventsPath, tradesPath) = RunSimInternal(cfg);
        return (eventsPath, tradesPath);
    }

    private static string RunSim(string cfg)
    {
        var (root, eventsPath, tradesPath) = RunSimInternal(cfg);
        return eventsPath;
    }

    private static (string root, string events, string trades) RunSimInternal(string cfg)
    {
        var solutionRoot = FindSolutionRoot();
        var dll = Path.Combine(solutionRoot, "src", "TiYf.Engine.Sim", "bin", "Release", "net8.0", "TiYf.Engine.Sim.dll");
        Assert.True(File.Exists(dll), "Sim DLL missing. Build Release first.");
        var psi = new ProcessStartInfo("dotnet", $"exec \"{dll}\" --config \"{cfg}\" --quiet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = solutionRoot };
        var p = Process.Start(psi)!; p.WaitForExit(60000);
        if (!p.HasExited) { try { p.Kill(); } catch { } throw new Exception("Sim timeout"); }
        Assert.Equal(0, p.ExitCode);
        var stdout = p.StandardOutput.ReadToEnd();
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? Extract(string key)
        {
            var match = lines.FirstOrDefault(l => l.StartsWith(key, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(match)) return null;
            return match.Substring(key.Length).Trim();
        }

        var runIdLine = Extract("RUN_ID=");
        var runId = string.IsNullOrWhiteSpace(runIdLine) ? "M0-RUN" : runIdLine;

        string ResolvePath(string? value, string fallbackRelative)
        {
            var rel = string.IsNullOrWhiteSpace(value) ? fallbackRelative : value;
            var normalized = rel.Replace('/', Path.DirectorySeparatorChar);
            try
            {
                return Path.GetFullPath(normalized, solutionRoot);
            }
            catch (Exception)
            {
                return Path.Combine(solutionRoot, normalized);
            }
        }

        var eventsPath = ResolvePath(Extract("JOURNAL_DIR_EVENTS="), Path.Combine("journals", "M0", runId, "events.csv"));
        var tradesPath = ResolvePath(Extract("JOURNAL_DIR_TRADES="), Path.Combine("journals", "M0", runId, "trades.csv"));
        Assert.True(File.Exists(eventsPath), $"Events journal not found: {eventsPath}\nSTDOUT:{stdout}\nSTDERR:{p.StandardError.ReadToEnd()}");
        Assert.True(File.Exists(tradesPath), $"Trades journal not found: {tradesPath}\nSTDOUT:{stdout}\nSTDERR:{p.StandardError.ReadToEnd()}");
        return (Path.GetDirectoryName(eventsPath) ?? solutionRoot, eventsPath, tradesPath);
    }

    private static string TempConfigWithRisk(string baseConfig, string riskMode)
    {
        var raw = File.ReadAllText(baseConfig);
        var root = System.Text.Json.Nodes.JsonNode.Parse(raw)!.AsObject();
        var flags = root["featureFlags"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        flags["risk"] = riskMode; // shadow
        root["featureFlags"] = flags;
        var rc = root["riskConfig"] as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        rc["emitEvaluations"] = true;
        rc["blockOnBreach"] = false; // we only observe evaluations
        if (!rc.ContainsKey("maxRunDrawdownCCY")) rc["maxRunDrawdownCCY"] = 999999.0;
        root["riskConfig"] = rc;
        var outPath = Path.Combine(Path.GetTempPath(), $"riskclear_{Guid.NewGuid():N}.json");
        File.WriteAllText(outPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
        return outPath;
    }

    private static string FindSolutionRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !File.Exists(Path.Combine(dir, "TiYf.Engine.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir ?? Directory.GetCurrentDirectory();
    }
}
