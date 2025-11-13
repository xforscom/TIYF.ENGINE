using System;
using System.IO;
using TiYf.Engine.Core.Slippage;
using TiYf.Engine.Sim;

namespace TiYf.Engine.Tests;

public sealed class SlippageConfigTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"slippage-config-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_DefaultsToZeroModel_WhenBlockMissing()
    {
        File.WriteAllText(_tempFile,
            """
            {
              "SchemaVersion":"1.1.0",
              "RunId":"RUN",
              "InstrumentFile":"sample-instruments.csv",
              "InputTicksFile":"sample-ticks.csv",
              "JournalRoot":"journals"
            }
            """);

        var (config, _, _) = EngineConfigLoader.Load(_tempFile);
        Assert.Null(config.Slippage);
        Assert.Equal("zero", SlippageModelFactory.Normalize(config.SlippageModel));
    }

    [Fact]
    public void Load_ParsesFixedBpsProfile()
    {
        File.WriteAllText(_tempFile,
            """
            {
              "SchemaVersion":"1.1.0",
              "RunId":"M8C",
              "InstrumentFile":"sample-instruments.csv",
              "InputTicksFile":"sample-ticks.csv",
              "JournalRoot":"journals",
              "Slippage": {
                "Model": "fixed_bps",
                "FixedBps": {
                  "DefaultBps": 1.5,
                  "Instruments": {
                    "EURUSD": 0.9,
                    "XAUUSD": 2.5
                  }
                }
              }
            }
            """);

        var (config, _, _) = EngineConfigLoader.Load(_tempFile);
        var profile = config.Slippage;
        Assert.NotNull(profile);
        Assert.Equal("fixed_bps", SlippageModelFactory.Normalize(profile!.Model));
        Assert.Equal(1.5m, profile.FixedBps!.DefaultBps);
        Assert.Equal(0.9m, profile.FixedBps!.Instruments!["EURUSD"]);
        Assert.Equal(2.5m, profile.FixedBps!.Instruments!["XAUUSD"]);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }
}
