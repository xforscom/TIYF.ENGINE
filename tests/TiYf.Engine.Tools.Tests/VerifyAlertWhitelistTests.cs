using System;
using System.IO;
using System.Text;
using TiYf.Engine.Tools;
using Xunit;

namespace TiYf.Engine.Tools.Tests;

public class VerifyAlertWhitelistTests
{
    [Fact]
    public void Verify_Accepts_Alert_Events()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "alert-whitelist-" + Guid.NewGuid().ToString("N") + ".csv");
        var sb = new StringBuilder();
        sb.AppendLine("schema_version=1.1.0,config_hash=ABC123");
        sb.AppendLine("sequence,utc_ts,event_type,payload_json");
        var now = DateTime.UtcNow.ToString("O");
        var payload = "{\"InstrumentId\":\"EURUSD\",\"DecisionId\":\"D1\",\"Observed\":7.0,\"Cap\":5.0}";
        var payloadEscaped = payload.Replace("\"", "\"\""); // CSV escape quotes
        sb.AppendLine($"1,{now},ALERT_BLOCK_LEVERAGE,\"{payloadEscaped}\"");
        File.WriteAllText(tmp, sb.ToString());
        var toolsProj = Path.Combine(FindSolutionRoot(), "src", "TiYf.Engine.Tools", "TiYf.Engine.Tools.csproj");
        Assert.True(File.Exists(toolsProj));
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"run --project \"{toolsProj}\" -- verify --file \"{tmp}\"")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new Xunit.Sdk.XunitException($"Verify CLI failed. Exit={proc.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
    }
    private static string FindSolutionRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !File.Exists(Path.Combine(dir, "TiYf.Engine.sln")))
        {
            var parent = Directory.GetParent(dir);
            dir = parent?.FullName;
        }
        return dir ?? Directory.GetCurrentDirectory();
    }
}
