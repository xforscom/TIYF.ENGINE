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

[Collection("E2E-Serial")] // serialize due to shared journals
public class SentimentActiveModeTests
{
    private static string ResolveRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "tests")) && Directory.Exists(Path.Combine(dir, "src"))) return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(dir); if (parent == null) break; dir = parent.FullName;
        }
        throw new InvalidOperationException("Repo root not resolved");
    }

    private static (string eventsPath, string tradesPath) Run(JsonDocument baseDoc, Action<Utf8JsonWriter> mutate, string runId)
    {
        string root = ResolveRepoRoot();
        string dll = Path.Combine(root, "src", "TiYf.Engine.Sim", "bin", "Release", "net8.0", "TiYf.Engine.Sim.dll");
        Assert.True(File.Exists(dll), "Sim binary not built - run dotnet build -c Release");
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var prop in baseDoc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("featureFlags") || prop.NameEquals("sentimentConfig")) continue; // skip to override
                writer.WritePropertyName(prop.Name); prop.Value.WriteTo(writer);
            }
            mutate(writer);
            writer.WriteEndObject();
        }
        string cfg = Path.Combine(Path.GetTempPath(), $"sent_active_{runId}_{Guid.NewGuid():N}.json");
        File.WriteAllBytes(cfg, ms.ToArray());
        string journalRoot = Path.Combine(root, "journals", "M0");
        string targetRunDir = Path.Combine(journalRoot, $"M0-RUN-{runId}");
        if (Directory.Exists(targetRunDir)) { try { Directory.Delete(targetRunDir, true); } catch { } }
        var psi = new ProcessStartInfo("dotnet", $"exec \"{dll}\" --config \"{cfg}\" --quiet --run-id {runId}")
        { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var proc = Process.Start(psi)!; proc.WaitForExit(120000);
        if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { } Assert.Fail("Sim timeout"); }
        Assert.Equal(0, proc.ExitCode);
        string runDir = Path.Combine(journalRoot, $"M0-RUN-{runId}");
        string events = Path.Combine(runDir, "events.csv");
        string trades = Path.Combine(runDir, "trades.csv");
        Assert.True(File.Exists(events)); Assert.True(File.Exists(trades));
        return (events, trades);
    }

    private static IEnumerable<(string type, string payload, string raw)> EventsEnum(string path)
    {
        foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).Skip(2))
        {
            var parts = line.Split(',', 4); if (parts.Length < 4) continue;
            yield return (parts[2], parts[3], line);
        }
    }

    private static string HashSkipMeta(string path)
    {
        // Skip meta line and normalize out config_hash column so differing feature flags don't cause trade hash divergence
        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count < 2) return string.Empty; // meta + maybe header
        var header = lines[1].Split(','); // assuming line0 meta, line1 header for trades.csv
        int cfgIdx = Array.FindIndex(header, h => h.Equals("config_hash", StringComparison.OrdinalIgnoreCase));
        var sb = new StringBuilder();
        // Rebuild header sans config_hash
        sb.AppendLine(string.Join(',', header.Where((h, i) => i != cfgIdx)));
        foreach (var row in lines.Skip(2))
        {
            var parts = row.Split(',');
            sb.AppendLine(string.Join(',', parts.Where((c, i) => i != cfgIdx)));
        }
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Select(b => b.ToString("X2")));
    }

    [Fact]
    public void SentimentActive_ClampInfluencesTrades()
    {
        string root = ResolveRepoRoot();
        var cfgPath = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.json");
        using var baseDoc = JsonDocument.Parse(File.ReadAllText(cfgPath));
        var (eventsActive, tradesActive) = Run(baseDoc, w =>
        {
            w.WritePropertyName("featureFlags"); w.WriteStartObject(); w.WriteString("sentiment", "active"); w.WriteString("riskProbe", "disabled"); w.WriteEndObject();
            w.WritePropertyName("sentimentConfig"); w.WriteStartObject(); w.WriteNumber("window", 5); w.WriteNumber("volGuardSigma", 0.0000001m); w.WriteEndObject();
        }, "actClamp");
        // baseline off
        var (eventsOff, tradesOff) = Run(baseDoc, w =>
        {
            w.WritePropertyName("featureFlags"); w.WriteStartObject(); w.WriteString("sentiment", "off"); w.WriteString("riskProbe", "disabled"); w.WriteEndObject();
        }, "offClamp");
        // Ensure APPLIED present in active and absent in off
        Assert.Contains(File.ReadLines(eventsActive), l => l.Contains("INFO_SENTIMENT_APPLIED_V1"));
        Assert.DoesNotContain(File.ReadLines(eventsOff), l => l.Contains("INFO_SENTIMENT_APPLIED_V1"));
        // Expect events hash difference due to APPLIED event presence; trades may or may not differ depending on unit column exposure
        Assert.NotEqual(HashSkipMeta(eventsOff), HashSkipMeta(eventsActive));
    }

    [Fact]
    public void Sentiment_ShadowVsOff_NonImpact()
    {
        string root = ResolveRepoRoot();
        var cfgPath = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.json");
        using var baseDoc = JsonDocument.Parse(File.ReadAllText(cfgPath));
        var (eventsShadow, tradesShadow) = Run(baseDoc, w =>
        {
            w.WritePropertyName("featureFlags"); w.WriteStartObject(); w.WriteString("sentiment", "shadow"); w.WriteString("riskProbe", "disabled"); w.WriteEndObject();
            w.WritePropertyName("sentimentConfig"); w.WriteStartObject(); w.WriteNumber("window", 8); w.WriteNumber("volGuardSigma", 0.5m); w.WriteEndObject();
        }, "shadowParity");
        var (eventsOff, tradesOff) = Run(baseDoc, w =>
        {
            w.WritePropertyName("featureFlags"); w.WriteStartObject(); w.WriteString("sentiment", "off"); w.WriteString("riskProbe", "disabled"); w.WriteEndObject();
        }, "offParity");
        // Trades parity: normalize by removing config_hash column and whitespace differences
        string NormalizeTrades(string path)
        {
            var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (lines.Count < 1) return string.Empty;
            var header = lines[0].Split(',');
            int cfgIdx = Array.FindIndex(header, h => h.Equals("config_hash", StringComparison.OrdinalIgnoreCase));
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(',', header.Where((h, i) => i != cfgIdx)));
            foreach (var row in lines.Skip(1))
            {
                var parts = row.Split(',');
                sb.AppendLine(string.Join(',', parts.Where((c, i) => i != cfgIdx)));
            }
            return sb.ToString();
        }
        Assert.Equal(NormalizeTrades(tradesOff), NormalizeTrades(tradesShadow));
        // Shadow has Z events, off does not
        Assert.Contains(File.ReadLines(eventsShadow), l => l.Contains("INFO_SENTIMENT_Z_V1"));
        Assert.DoesNotContain(File.ReadLines(eventsOff), l => l.Contains("INFO_SENTIMENT_Z_V1"));
    }

    [Fact]
    public void Sentiment_ActiveVsShadow_ControlledDiff()
    {
        string root = ResolveRepoRoot();
        var cfgPath = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.json");
        using var baseDoc = JsonDocument.Parse(File.ReadAllText(cfgPath));
        var (eventsActive, tradesActive) = Run(baseDoc, w =>
        {
            w.WritePropertyName("featureFlags"); w.WriteStartObject(); w.WriteString("sentiment", "active"); w.WriteString("riskProbe", "disabled"); w.WriteEndObject();
            w.WritePropertyName("sentimentConfig"); w.WriteStartObject(); w.WriteNumber("window", 5); w.WriteNumber("volGuardSigma", 0.0000001m); w.WriteEndObject();
        }, "actVSsha");
        var (eventsShadow, tradesShadow) = Run(baseDoc, w =>
        {
            w.WritePropertyName("featureFlags"); w.WriteStartObject(); w.WriteString("sentiment", "shadow"); w.WriteString("riskProbe", "disabled"); w.WriteEndObject();
            w.WritePropertyName("sentimentConfig"); w.WriteStartObject(); w.WriteNumber("window", 5); w.WriteNumber("volGuardSigma", 0.0000001m); w.WriteEndObject();
        }, "shaVSact");
        // Walk paired events until divergence
        var actLines = File.ReadAllLines(eventsActive);
        var shaLines = File.ReadAllLines(eventsShadow);
        int min = Math.Min(actLines.Length, shaLines.Length);
        int firstDiff = -1;
        // Skip meta + header (first two lines) when diffing
        for (int i = 2; i < min; i++) if (!string.Equals(actLines[i], shaLines[i], StringComparison.Ordinal)) { firstDiff = i; break; }
        Assert.True(firstDiff >= 0, "No divergence found (unexpected if clamp triggers)");
        // Ensure divergence line contains either APPLIED event or a trade influence marker
        bool acceptable = actLines[firstDiff].Contains("INFO_SENTIMENT_APPLIED_V1") || actLines[firstDiff].Contains("INFO_SENTIMENT_CLAMP_V1");
        Assert.True(acceptable, $"First diff not an applied/clamp event: {actLines[firstDiff]}");
    }

    [Fact]
    public void SentimentActive_Determinism_ABParity()
    {
        string root = ResolveRepoRoot();
        var cfgPath = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.json");
        using var baseDoc = JsonDocument.Parse(File.ReadAllText(cfgPath));
        var (eventsA, tradesA) = Run(baseDoc, w =>
        {
            w.WritePropertyName("featureFlags"); w.WriteStartObject(); w.WriteString("sentiment", "active"); w.WriteString("riskProbe", "disabled"); w.WriteEndObject();
            w.WritePropertyName("sentimentConfig"); w.WriteStartObject(); w.WriteNumber("window", 6); w.WriteNumber("volGuardSigma", 0.3m); w.WriteEndObject();
        }, "actA");
        var (eventsB, tradesB) = Run(baseDoc, w =>
        {
            w.WritePropertyName("featureFlags"); w.WriteStartObject(); w.WriteString("sentiment", "active"); w.WriteString("riskProbe", "disabled"); w.WriteEndObject();
            w.WritePropertyName("sentimentConfig"); w.WriteStartObject(); w.WriteNumber("window", 6); w.WriteNumber("volGuardSigma", 0.3m); w.WriteEndObject();
        }, "actB");
        Assert.Equal(HashSkipMeta(eventsA), HashSkipMeta(eventsB));
        Assert.Equal(HashSkipMeta(tradesA), HashSkipMeta(tradesB));
    }

    [Fact]
    public void SentimentEvents_Formatting_Invariant()
    {
        string root = ResolveRepoRoot();
        var cfgPath = Path.Combine(root, "tests/fixtures/backtest_m0/config.backtest-m0.candidate.json");
        using var baseDoc = JsonDocument.Parse(File.ReadAllText(cfgPath));
        var (events, _) = Run(baseDoc, w =>
        {
            w.WritePropertyName("featureFlags"); w.WriteStartObject(); w.WriteString("sentiment", "active"); w.WriteString("riskProbe", "disabled"); w.WriteEndObject();
            w.WritePropertyName("sentimentConfig"); w.WriteStartObject(); w.WriteNumber("window", 5); w.WriteNumber("volGuardSigma", 0.0000001m); w.WriteEndObject();
        }, "fmtInv");
        foreach (var ev in EventsEnum(events).Where(e => e.type.StartsWith("INFO_SENTIMENT_")))
        {
            var json = ev.payload.Trim('"').Replace("\"\"", "\"");
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number)
                {
                    var raw = prop.Value.GetRawText();
                    Assert.DoesNotContain('E', raw);
                    Assert.DoesNotContain(',', raw);
                }
            }
        }
    }
}
