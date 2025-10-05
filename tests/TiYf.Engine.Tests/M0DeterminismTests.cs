using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using TiYf.Engine.Core;
using Xunit;

namespace TiYf.Engine.Tests;

public class M0DeterminismTests
{
    private const string ExpectedDataVersion = "C531EDAA1B2B3EB9286B3EDA98B6443DD365C1A8DFEA2AFB4B77FC7DDD1D6122";

    [Fact]
    public void BacktestM0_EventsAndTrades_BitExactAcrossTwoRuns()
    {
        // This test intentionally runs the engine multiple times and reuses the fixed journal output path
        // cleaned by the program before each run. We copy the artifacts after a run, then run again, and
        // compare canonical hashes to guarantee bit-exact determinism.
        var config = Path.Combine("tests","fixtures","backtest_m0","config.backtest-m0.json");
        Assert.True(File.Exists(config));
        var tmpRoot = Path.Combine(Path.GetTempPath(), "m0-determinism-tests");
        if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true);
        Directory.CreateDirectory(tmpRoot);

        string Run(string tag)
        {
            var runDir = Path.Combine(tmpRoot, tag);
            Directory.CreateDirectory(runDir);
            var psi = new ProcessStartInfo("dotnet", $"run --project src/TiYf.Engine.Sim -- --config {config}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            var proc = Process.Start(psi)!;
            proc.WaitForExit();
            Assert.Equal(0, proc.ExitCode);
            return runDir;
        }

        // Run twice
        Run("A");
        Run("B");

        var journalBase = Path.Combine("journals","M0","M0-RUN"); // engine writes here deterministically each run (cleaned before run)
        Assert.True(Directory.Exists(journalBase));
        var eventsPath = Path.Combine(journalBase, "events.csv");
        var tradesPath = Path.Combine(journalBase, "trades.csv");
        Assert.True(File.Exists(eventsPath));
        Assert.True(File.Exists(tradesPath));

        // Capture copy after run A
        var copyEventsA = Path.Combine(tmpRoot, "eventsA.csv");
        var copyTradesA = Path.Combine(tmpRoot, "tradesA.csv");
        File.Copy(eventsPath, copyEventsA, true);
        File.Copy(tradesPath, copyTradesA, true);

        // Re-run to produce B fresh
        Run("C");
        var copyEventsB = Path.Combine(tmpRoot, "eventsB.csv");
        var copyTradesB = Path.Combine(tmpRoot, "tradesB.csv");
        File.Copy(eventsPath, copyEventsB, true);
        File.Copy(tradesPath, copyTradesB, true);

        string Hash(string p)
        {
            var raw = File.ReadAllBytes(p);
            var canon = CsvCanonicalizer.Canonicalize(raw);
            return CsvCanonicalizer.Sha256Hex(canon);
        }

        var hEventsA = Hash(copyEventsA);
        var hEventsB = Hash(copyEventsB);
        var hTradesA = Hash(copyTradesA);
        var hTradesB = Hash(copyTradesB);

        Assert.Equal(hEventsA, hEventsB);
        Assert.Equal(hTradesA, hTradesB);

        // Meta checks events
        var eventsLines = File.ReadAllLines(copyEventsA);
        Assert.True(eventsLines.Length > 2);
        var meta = eventsLines[0];
        Assert.Contains("data_version="+ExpectedDataVersion, meta);
        Assert.Contains("schema_version=", meta);
        Assert.Contains("config_hash=", meta);
        // zero alerts
        Assert.DoesNotContain(eventsLines.Skip(1), l => l.Contains("ALERT_BLOCK_"));

        var tradesLines = File.ReadAllLines(copyTradesA);
        Assert.Equal(7, tradesLines.Length); // header + 6 rows
        var eurBuy = tradesLines.Skip(1).First(r=> r.Contains("M0-EURUSD-01"));
        Assert.Contains("2025-01-02T00:15:00Z", eurBuy);
        Assert.Contains("2025-01-02T00:45:00Z", eurBuy);
        // price precision 5 decimals EURUSD
        var parts = eurBuy.Split(',');
        Assert.Matches(@"^\d+\.\d{5}$", parts[4]);
        Assert.Matches(@"^\d+\.\d{5}$", parts[5]);
        Assert.Matches(@"^-?\d+\.\d{2}$", parts[7]);
    }
}
