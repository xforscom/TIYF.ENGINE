using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TiYf.Engine.Tests;

[Collection("E2E-Serial")] // serialize due to journals
public class SentimentGuardIntegrationTests
{
    private static string ResolveRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i=0;i<12;i++)
        {
            if (Directory.Exists(Path.Combine(dir, "tests")) && Directory.Exists(Path.Combine(dir, "src"))) return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(dir); if (parent==null) break; dir = parent.FullName;
        }
        throw new InvalidOperationException("Repo root not resolved");
    }

    private static (string eventsPath, string tradesPath) RunSimWithConfig(JsonDocument baseDoc, Action<Utf8JsonWriter>? mutate, string runId)
    {
        string root = ResolveRepoRoot();
        string buildDir = Path.Combine(root, "src","TiYf.Engine.Sim","bin","Release","net8.0");
        string exe = Path.Combine(buildDir, "TiYf.Engine.Sim.dll");
        Assert.True(File.Exists(exe), "Sim binary not built (dotnet build -c Release)");
        // Write temp mutated config
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions{Indented=true}))
        {
            writer.WriteStartObject();
            foreach (var prop in baseDoc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("featureFlags"))
                {
                    writer.WritePropertyName("featureFlags");
                    writer.WriteStartObject();
                    // copy existing and allow mutation later by captured writer
                    foreach (var ff in prop.Value.EnumerateObject())
                    {
                        writer.WritePropertyName(ff.Name); ff.Value.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                else if (prop.NameEquals("sentimentConfig"))
                {
                    // skip if present; we'll add later to control ordering
                }
                else { writer.WritePropertyName(prop.Name); prop.Value.WriteTo(writer); }
            }
            // custom mutations (add/adjust sentiment related fields)
            mutate?.Invoke(writer);
            writer.WriteEndObject();
        }
        string tempCfg = Path.Combine(Path.GetTempPath(), $"sent_{runId}_{Guid.NewGuid():N}.json");
        File.WriteAllBytes(tempCfg, ms.ToArray());
        string journalRoot = Path.Combine(root, "journals","M0");
        string targetRunDir = Path.Combine(journalRoot, $"M0-RUN-{runId}");
        if (Directory.Exists(targetRunDir)) { try { Directory.Delete(targetRunDir, true);} catch {} }
        var args = $"exec \"{exe}\" --config \"{tempCfg}\" --quiet --run-id {runId}";
        var psi = new ProcessStartInfo("dotnet", args){ WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true};
        var proc = Process.Start(psi)!; proc.WaitForExit(120_000);
        if (!proc.HasExited) { try { proc.Kill(entireProcessTree:true);} catch {} Assert.Fail("Sim timeout"); }
        if (proc.ExitCode != 0)
        {
            string stdout = string.Empty; string stderr = string.Empty;
            try { stdout = proc.StandardOutput.ReadToEnd(); } catch {}
            try { stderr = proc.StandardError.ReadToEnd(); } catch {}
            var diag = new StringBuilder();
            diag.AppendLine($"Sim failed exit={proc.ExitCode} run={runId}");
            if (!string.IsNullOrWhiteSpace(stdout)) { diag.AppendLine("-- STDOUT --"); diag.AppendLine(stdout); }
            if (!string.IsNullOrWhiteSpace(stderr)) { diag.AppendLine("-- STDERR --"); diag.AppendLine(stderr); }
            Assert.Fail(diag.ToString());
        }
        string events = Path.Combine(targetRunDir, "events.csv");
        string trades = Path.Combine(targetRunDir, "trades.csv");
        Assert.True(File.Exists(events)); Assert.True(File.Exists(trades));
        return (events, trades);
    }

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return string.Concat(sha.ComputeHash(fs).Select(b=>b.ToString("X2")));
    }

    private static IEnumerable<string> EventLines(string eventsCsv) => File.ReadLines(eventsCsv).Where(l=>!string.IsNullOrWhiteSpace(l));

    private static IEnumerable<(string type,string json,string raw)> EnumerateEvents(string eventsCsv)
    {
        // Skip meta + header -> data rows start at index2
        foreach (var line in EventLines(eventsCsv).Skip(2))
        {
            var firstComma = line.IndexOf(','); if (firstComma<0) continue;
            var second = line.IndexOf(',', firstComma+1); if (second<0) continue;
            var third = line.IndexOf(',', second+1); if (third<0) continue;
            var type = line.Substring(second+1, third-second-1);
            var payload = line.Substring(third+1);
            yield return (type, payload, line);
        }
    }

    [Fact]
    public void SentimentClamp_Occurs_WithUltraLowSigma()
    {
        string root = ResolveRepoRoot();
        var cfgPath = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.json");
        using var baseDoc = JsonDocument.Parse(File.ReadAllText(cfgPath));
        var (events, trades) = RunSimWithConfig(baseDoc, writer => {
            // enable shadow sentiment + tiny sigma for clamp
            writer.WritePropertyName("featureFlags");
            writer.WriteStartObject();
            writer.WriteString("sentiment", "shadow");
            writer.WriteString("learning", "disabled");
            writer.WriteString("riskProbe", "disabled");
            writer.WriteEndObject();
            writer.WritePropertyName("sentimentConfig");
            writer.WriteStartObject();
            writer.WriteNumber("window", 5);
            writer.WriteNumber("volGuardSigma", 0.0000001m); // ultra-low to trigger clamp easily
            writer.WriteEndObject();
        }, "clamp");
        // trades row count (header + 6 trades)
        int tradeRows = File.ReadAllLines(trades).Count(l=>!string.IsNullOrWhiteSpace(l)) - 1;
        Assert.Equal(6, tradeRows);
        var types = EnumerateEvents(events).Select(e=>e.type).ToList();
        Assert.Contains("INFO_SENTIMENT_Z_V1", types);
        Assert.Contains("INFO_SENTIMENT_CLAMP_V1", types); // at least one clamp
        // ordering: Z immediately followed by optional CLAMP with same timestamp — we ensure any CLAMP has a preceding Z in file order
        var lines = EventLines(events).ToList();
        int firstDataIndex = 2; // meta + header
        bool clampHadPriorZ = true;
        for (int i=firstDataIndex;i<lines.Count;i++)
        {
            if (lines[i].Contains("INFO_SENTIMENT_CLAMP_V1", StringComparison.Ordinal))
            {
                // search backwards to nearest BAR or Z
                bool foundZ = false;
                for (int j=i-1;j>=firstDataIndex;j--)
                {
                    if (lines[j].Contains("INFO_SENTIMENT_Z_V1", StringComparison.Ordinal)) { foundZ = true; break; }
                    if (lines[j].Contains("BAR_V1", StringComparison.Ordinal)) break; // shouldn't jump across bar boundary without Z
                }
                if (!foundZ) { clampHadPriorZ = false; break; }
            }
        }
        Assert.True(clampHadPriorZ, "Clamp event without preceding Z event in sequence");
        // payload formatting invariants for sampled sentiment events
        foreach (var ev in EnumerateEvents(events).Where(e=>e.type.StartsWith("INFO_SENTIMENT_")))
        {
            var json = ev.json.Trim('"').Replace("\"\"", "\"");
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number)
                {
                    var rawNum = prop.Value.GetRawText();
                    Assert.DoesNotContain('E', rawNum); // no scientific notation
                    Assert.DoesNotContain(',', rawNum); // invariant decimal separator
                }
            }
        }
    }

    [Fact]
    public void SentimentShadow_Determinism_TwoRuns()
    {
        string root = ResolveRepoRoot();
        var cfgPath = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.json");
        using var baseDoc = JsonDocument.Parse(File.ReadAllText(cfgPath));
        (string eventsA, string tradesA) = RunSimWithConfig(baseDoc, writer => {
            writer.WritePropertyName("featureFlags");
            writer.WriteStartObject();
            writer.WriteString("sentiment", "shadow");
            writer.WriteString("learning", "disabled");
            writer.WriteString("riskProbe", "disabled");
            writer.WriteEndObject();
            writer.WritePropertyName("sentimentConfig");
            writer.WriteStartObject(); writer.WriteNumber("window", 8); writer.WriteNumber("volGuardSigma", 0.25m); writer.WriteEndObject();
        }, "sentA");
        (string eventsB, string tradesB) = RunSimWithConfig(baseDoc, writer => {
            writer.WritePropertyName("featureFlags");
            writer.WriteStartObject();
            writer.WriteString("sentiment", "shadow");
            writer.WriteString("learning", "disabled");
            writer.WriteString("riskProbe", "disabled");
            writer.WriteEndObject();
            writer.WritePropertyName("sentimentConfig");
            writer.WriteStartObject(); writer.WriteNumber("window", 8); writer.WriteNumber("volGuardSigma", 0.25m); writer.WriteEndObject();
        }, "sentB");
        Assert.Equal(Sha256(eventsA), Sha256(eventsB));
        Assert.Equal(Sha256(tradesA), Sha256(tradesB));
    }

    [Fact]
    public void SentimentShadow_NonImpact_TradesParityWithBaseline()
    {
        string root = ResolveRepoRoot();
        var cfgPath = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.json");
        using var baseDoc = JsonDocument.Parse(File.ReadAllText(cfgPath));
        // baseline (sentiment disabled)
        (string eventsBase, string tradesBase) = RunSimWithConfig(baseDoc, writer => {
            writer.WritePropertyName("featureFlags");
            writer.WriteStartObject();
            writer.WriteString("sentiment", "disabled");
            writer.WriteString("learning", "disabled");
            writer.WriteString("riskProbe", "disabled");
            writer.WriteEndObject();
        }, "nosent");
        // shadow enabled
        (string eventsShadow, string tradesShadow) = RunSimWithConfig(baseDoc, writer => {
            writer.WritePropertyName("featureFlags");
            writer.WriteStartObject();
            writer.WriteString("sentiment", "shadow");
            writer.WriteString("learning", "disabled");
            writer.WriteString("riskProbe", "disabled");
            writer.WriteEndObject();
            writer.WritePropertyName("sentimentConfig");
            writer.WriteStartObject(); writer.WriteNumber("window", 10); writer.WriteNumber("volGuardSigma", 0.15m); writer.WriteEndObject();
        }, "withsent");
        // Hash trades ignoring config_hash column (economic & formatting parity)
        static string NormalizedTradesHash(string path)
        {
            var lines = File.ReadAllLines(path).Where(l=>!string.IsNullOrWhiteSpace(l)).ToList();
            if (lines.Count==0) return string.Empty;
            var header = lines[0].Split(',');
            int cfgIdx = Array.FindIndex(header, h=>h.Equals("config_hash", StringComparison.OrdinalIgnoreCase));
            var sb = new StringBuilder();
            // Rebuild header without config_hash for normalization
            sb.AppendLine(string.Join(',', header.Where((h,i)=>i!=cfgIdx)));
            foreach (var row in lines.Skip(1))
            {
                var parts = row.Split(',');
                sb.AppendLine(string.Join(',', parts.Where((c,i)=>i!=cfgIdx)));
            }
            using var sha = SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            return string.Concat(sha.ComputeHash(bytes).Select(b=>b.ToString("X2")));
        }
        Assert.Equal(NormalizedTradesHash(tradesBase), NormalizedTradesHash(tradesShadow));
        // Ensure Z events exist only in shadow
        Assert.DoesNotContain(EventLines(eventsBase), l=>l.Contains("INFO_SENTIMENT_Z_V1", StringComparison.Ordinal));
        Assert.Contains(EventLines(eventsShadow), l=>l.Contains("INFO_SENTIMENT_Z_V1", StringComparison.Ordinal));
    }
}
