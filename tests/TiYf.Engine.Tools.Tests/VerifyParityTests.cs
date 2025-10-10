using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace TiYf.Engine.Tools.Tests;

public class VerifyParityTests
{
        private static string ToolsDll()
        {
                var rel = Path.Combine(Directory.GetCurrentDirectory(), "src", "TiYf.Engine.Tools", "bin", "Release", "net8.0", "TiYf.Engine.Tools.dll");
                if (File.Exists(rel)) return rel;
                var dbg = Path.Combine(Directory.GetCurrentDirectory(), "src", "TiYf.Engine.Tools", "bin", "Debug", "net8.0", "TiYf.Engine.Tools.dll");
                if (File.Exists(dbg)) return dbg;
                throw new FileNotFoundException("Tools CLI not built");
        }

        private static (string ev, string tr) MakeJournal(string tag, string payload)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"parity_{Guid.NewGuid():N}_{tag}");
        Directory.CreateDirectory(dir);
        var ev = Path.Combine(dir, "events.csv");
        var tr = Path.Combine(dir, "trades.csv");
        var json = System.Text.Json.JsonSerializer.Serialize(new { symbol = "EURUSD", z = 0, window = 5, sigma = 0.1 });
        var csvPayload = '"' + json.Replace("\"", "\"\"") + '"';
        var sb = new StringBuilder();
        sb.AppendLine("schema_version=1.3.0,config_hash=ABC");
        sb.AppendLine("sequence,utc_ts,event_type,payload_json");
        sb.Append("1,2025-01-01T00:00:00Z,INFO_SENTIMENT_Z_V1,").Append(csvPayload).Append('\n');
        File.WriteAllText(ev, sb.ToString(), Encoding.UTF8);
        File.WriteAllText(tr, "utc_ts_open,utc_ts_close,symbol,direction,entry_price,exit_price,volume_units,pnl_ccy,pnl_r,decision_id,schema_version,config_hash,data_version\n2025-01-01T00:00:00Z,2025-01-01T00:00:00Z,EURUSD,BUY,1.1,1.1,100,0,0,DEC1,1.3.0,ANY,\n", Encoding.UTF8);
        return (ev, tr);
    }

    private static string Exec(string args)
    {
        var dll = ToolsDll();
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"exec \"{dll}\" {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(10000);
        return $"EXIT={p.ExitCode}\n" + p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    }

    [Fact]
    public void Parity_Match_Exit0()
    {
        var (ev, tr) = MakeJournal("A", "{}");
        var outp = Exec($"verify parity --events-a \"{ev}\" --events-b \"{ev}\" --trades-a \"{tr}\" --trades-b \"{tr}\"");
        Assert.Contains("EXIT=0", outp);
        Assert.Contains("PARITY events: OK", outp);
        Assert.Contains("PARITY trades: OK", outp);
    }

    [Fact]
    public void Parity_Mismatch_Exit2_FirstDiff()
    {
        var (ev, tr) = MakeJournal("A", "{}");
        var (ev2, tr2) = MakeJournal("B", "{}");
        // Change one line in events
        var lines = File.ReadAllLines(ev2);
        lines[2] = lines[2].Replace("EURUSD", "USDJPY");
        File.WriteAllLines(ev2, lines);
        var outp = Exec($"verify parity --events-a \"{ev}\" --events-b \"{ev2}\"");
        Assert.Contains("EXIT=2", outp);
        Assert.Contains("PARITY events: MISMATCH", outp);
        Assert.Contains("FIRST_DIFF line=3", outp);
    }
}
