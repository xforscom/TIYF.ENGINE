using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TiYf.Engine.Sim;
using Xunit;

namespace TiYf.Engine.Tests;

public sealed class DemoTradingHarnessTests
{
    // Provide isolation for file-system heavy harness execution
    [Fact]
    public async Task RunAsync_ProducesResultWithExpectedShape()
    {
        string root = ResolveRepoRoot();
    string configSource = Path.Combine(root, "tests", "fixtures", "backtest_m0", "config.backtest-m0.json");
    string tempRoot = Path.Combine(Path.GetTempPath(), "demo-harness-tests", Guid.NewGuid().ToString("N"));
    string journalRoot = Path.Combine(tempRoot, "journals");
    string configCopy = Path.Combine(tempRoot, "config.json");

    Directory.CreateDirectory(tempRoot);
    Directory.CreateDirectory(journalRoot);

    PrepareConfig(configSource, configCopy, root);

        try
        {
            var options = new DemoTradingOptions(
                ConfigPath: configCopy,
                RunId: "unit",
                JournalRoot: journalRoot,
                ZipArtifacts: false);

            DemoTradingResult result = await DemoTradingHarness.RunAsync(options);

            Assert.NotNull(result);
            Assert.StartsWith("DEMO-RUN-", result.RunId, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result.EventsPath), "events journal missing");
            Assert.True(File.Exists(result.TradesPath), "trades journal missing");
            Assert.True(File.Exists(result.StrictReportPath), "strict report missing");
            Assert.True(File.Exists(result.ParityReportPath), "parity report missing");
            Assert.True(File.Exists(result.EnvSanityPath), "env sanity snapshot missing");
            Assert.Null(result.ZipPath);
            Assert.Equal(0, result.StrictExitCode);
            Assert.Equal(0, result.ParityExitCode);
        }
        finally
        {
            bool preserve = string.Equals(Environment.GetEnvironmentVariable("TIYF_PRESERVE_DEMO_HARNESS"), "1", StringComparison.Ordinal);
            if (!preserve && Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch
                {
                    // best effort cleanup; ignore failures to keep test noise low
                }
            }
        }
    }

    private static void PrepareConfig(string source, string destination, string repoRoot)
    {
        var node = JsonNode.Parse(File.ReadAllText(source))?.AsObject()
            ?? throw new InvalidOperationException("Unable to parse demo config template");

        if (!node.TryGetPropertyValue("SchemaVersion", out _))
        {
            node["SchemaVersion"] = "1.3.0";
        }

        if (node.TryGetPropertyValue("data", out var dataNode) && dataNode is JsonObject dataObj)
        {
            if (dataObj.TryGetPropertyValue("instrumentsFile", out var instrumentsValue) && instrumentsValue is JsonValue instVal && instVal.TryGetValue<string>(out var instPath) && !string.IsNullOrWhiteSpace(instPath))
            {
                dataObj["instrumentsFile"] = Path.GetFullPath(Path.Combine(repoRoot, instPath));
            }

            if (dataObj.TryGetPropertyValue("ticks", out var ticksValue) && ticksValue is JsonObject ticksObj)
            {
                foreach (var kvp in ticksObj.ToList())
                {
                    if (kvp.Value is JsonValue tickVal && tickVal.TryGetValue<string>(out var rel) && !string.IsNullOrWhiteSpace(rel))
                    {
                        ticksObj[kvp.Key] = Path.GetFullPath(Path.Combine(repoRoot, rel));
                    }
                }
            }
        }

        File.WriteAllText(destination, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
    }

    private static string ResolveRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src")) && File.Exists(Path.Combine(dir, "TiYf.Engine.sln")))
            {
                return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            var parent = Directory.GetParent(dir);
            if (parent is null)
            {
                break;
            }

            dir = parent.FullName;
        }

        throw new InvalidOperationException("Cannot resolve repository root from test context");
    }
}
