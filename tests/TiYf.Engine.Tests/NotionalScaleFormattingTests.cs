using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace TiYf.Engine.Tests;

[Collection("E2E-Serial")] // ensure no journal contention
public class NotionalScaleFormattingTests
{
    private static string ResolveRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "tests")) && Directory.Exists(Path.Combine(dir, "src"))) return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(dir); if (parent==null) break; dir = parent.FullName;
        }
        throw new InvalidOperationException("Repo root not resolved");
    }

    private record RunResult(string TradesPath, string EventsPath, string ConfigHash, string? DataVersion, string SchemaVersion);

    private static RunResult RunSim(string cfgPath, string runId)
    {
        string root = ResolveRepoRoot();
        string buildDir = Path.Combine(root, "src","TiYf.Engine.Sim","bin","Release","net8.0");
        string exe = Path.Combine(buildDir, "TiYf.Engine.Sim.dll");
        Assert.True(File.Exists(exe), "Sim binary not built (dotnet build -c Release)");
        string journalRoot = Path.Combine(root, "journals","M0");
        string targetRunDir = Path.Combine(journalRoot, $"M0-RUN-{runId}");
        if (Directory.Exists(targetRunDir)) { try { Directory.Delete(targetRunDir, true); } catch { } }
        string tempCfg = Path.Combine(Path.GetTempPath(), $"scale_{runId}_{Guid.NewGuid():N}.json");
        File.Copy(cfgPath, tempCfg, true);
        var args = $"exec \"{exe}\" --config \"{tempCfg}\" --quiet --run-id {runId}";
        var psi = new ProcessStartInfo("dotnet", args){ WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true};
        var proc = Process.Start(psi)!; proc.WaitForExit();
        if (proc.ExitCode != 0) Assert.Fail($"Sim failed exit={proc.ExitCode} args={args}");
        string trades = Path.Combine(targetRunDir, "trades.csv");
        string events = Path.Combine(targetRunDir, "events.csv");
        Assert.True(File.Exists(trades), "trades.csv missing");
        Assert.True(File.Exists(events), "events.csv missing");
        // Extract meta from events first data row JSON
        string schemaVersion = ""; string cfgHash = ""; string? dataVersion = null;
        var lines = File.ReadAllLines(events).Where(l=>!string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count >= 3)
        {
            var firstData = lines[2]; // meta line index0, header index1
            var parts = SplitCsv(firstData);
            if (parts.Length >= 4)
            {
                var jsonRaw = parts[3].Trim('"').Replace("\"\"", "\"");
                try {
                    using var doc = JsonDocument.Parse(jsonRaw);
                    if (doc.RootElement.TryGetProperty("schema_version", out var sv) && sv.ValueKind==JsonValueKind.String) schemaVersion = sv.GetString()!;
                    if (doc.RootElement.TryGetProperty("config_hash", out var ch) && ch.ValueKind==JsonValueKind.String) cfgHash = ch.GetString()!;
                    if (doc.RootElement.TryGetProperty("data_version", out var dv) && dv.ValueKind==JsonValueKind.String) dataVersion = dv.GetString();
                } catch { }
            }
        }
        return new RunResult(trades, events, cfgHash, dataVersion, schemaVersion);
    }

    private static string[] SplitCsv(string line)
    {
        var arr = new List<string>(); int commas=3; int last=0;
        for (int i=0;i<line.Length && commas>0;i++) if (line[i]==','){ arr.Add(line.Substring(last,i-last)); last=i+1; commas--; }
        arr.Add(line.Substring(last)); return arr.ToArray();
    }

    private static void AssertTradesFormatting(string tradesCsv)
    {
        var lines = File.ReadAllLines(tradesCsv).Where(l=>!string.IsNullOrWhiteSpace(l)).ToList();
        Assert.True(lines.Count > 1, "No trade rows");
        var header = lines[0].Split(',');
        // header invariants (just ensure column names present string-wise)
        var headerJoined = string.Join(',', header);
        Assert.True(headerJoined.Contains("schema_version", StringComparison.OrdinalIgnoreCase), "schema_version missing in header");
        Assert.True(headerJoined.Contains("config_hash", StringComparison.OrdinalIgnoreCase), "config_hash missing in header");
        Assert.True(headerJoined.Contains("data_version", StringComparison.OrdinalIgnoreCase), "data_version missing in header");
        int idxEntry = Array.IndexOf(header, "entry_price");
        int idxExit = Array.IndexOf(header, "exit_price");
        int idxVol = Array.IndexOf(header, "volume_units");
        int idxPnl = Array.IndexOf(header, "pnl_ccy");
        Assert.True(idxEntry>=0 && idxExit>=0 && idxVol>=0 && idxPnl>=0, "Expected columns missing");
        var pricePrecisionPerSymbol = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        var pnlRegex = new Regex(@"^-?\d+\.\d{2}$", RegexOptions.Compiled);
        foreach (var row in lines.Skip(1))
        {
            var parts = row.Split(',');
            if (parts.Length <= Math.Max(idxPnl, idxExit)) continue;
            string entry = parts[idxEntry]; string exitP = parts[idxExit]; string vol = parts[idxVol]; string pnl = parts[idxPnl]; string symbol = parts[2];
            // No blanks
            Assert.False(string.IsNullOrWhiteSpace(entry) || string.IsNullOrWhiteSpace(exitP) || string.IsNullOrWhiteSpace(vol) || string.IsNullOrWhiteSpace(pnl) || string.IsNullOrWhiteSpace(symbol), $"Blank field in row {row}");
            // No scientific notation
            Assert.DoesNotContain('E', entry);
            Assert.DoesNotContain('E', exitP);
            Assert.DoesNotContain('E', pnl);
            // Decimal separator '.' only
            if (entry.Contains('.')) Assert.DoesNotContain(',', entry);
            if (exitP.Contains('.')) Assert.DoesNotContain(',', exitP);
            if (pnl.Contains('.')) Assert.DoesNotContain(',', pnl);
            // pnl two decimals
            Assert.Matches(pnlRegex, pnl);
            // volume integer
            Assert.True(long.TryParse(vol, NumberStyles.Integer, CultureInfo.InvariantCulture, out _), $"Volume not integer: {vol}");
            // price precision stable per symbol
            int EntryDecs(string s){ var i=s.IndexOf('.'); return i<0?0:s.Length-i-1; }
            var decs = EntryDecs(entry);
            if (!pricePrecisionPerSymbol.TryGetValue(symbol, out var existing)) pricePrecisionPerSymbol[symbol]=decs; else Assert.Equal(existing, decs);
            // exit price matches same precision
            Assert.Equal(pricePrecisionPerSymbol[symbol], EntryDecs(exitP));
            // No NaN/Infinity
            Assert.DoesNotContain("NaN", row, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Infinity", row, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NotionalScale_BaseFormatting_Invariants()
    {
        string root = ResolveRepoRoot();
        var cfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.json");
        var run = RunSim(cfg, "scaleBase");
        AssertTradesFormatting(run.TradesPath);
    }

    [Fact]
    public void NotionalScale_ScaledSize_FormattingInvariantsHold()
    {
        string root = ResolveRepoRoot();
        var baseCfg = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.json");
        // Make a temp copy adjusting sizeUnitsFx to a different integer (e.g., 5000) to simulate scaling
        var json = File.ReadAllText(baseCfg);
        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions{Indented=true}))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("strategy"))
                {
                    writer.WritePropertyName("strategy");
                    writer.WriteStartObject();
                    // Copy strategy properties, mutating params
                    foreach (var stratProp in prop.Value.EnumerateObject())
                    {
                        if (stratProp.NameEquals("params"))
                        {
                            writer.WritePropertyName("params");
                            writer.WriteStartObject();
                            bool wroteSize = false;
                            foreach (var p in stratProp.Value.EnumerateObject())
                            {
                                if (p.NameEquals("sizeUnitsFx")) { writer.WriteNumber("sizeUnitsFx", 5000); wroteSize = true; }
                                else { writer.WritePropertyName(p.Name); p.Value.WriteTo(writer); }
                            }
                            if (!wroteSize) writer.WriteNumber("sizeUnitsFx", 5000);
                            writer.WriteEndObject();
                        }
                        else { writer.WritePropertyName(stratProp.Name); stratProp.Value.WriteTo(writer); }
                    }
                    writer.WriteEndObject();
                }
                else { writer.WritePropertyName(prop.Name); prop.Value.WriteTo(writer); }
            }
            writer.WriteEndObject();
        }
        var mutated = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        string tempScaled = Path.Combine(Path.GetTempPath(), $"scale_cfg_{Guid.NewGuid():N}.json");
        File.WriteAllText(tempScaled, mutated);
        var scaledRun = RunSim(tempScaled, "scaleBig");
        AssertTradesFormatting(scaledRun.TradesPath);
    }
}
