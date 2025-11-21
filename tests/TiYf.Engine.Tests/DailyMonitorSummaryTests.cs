using System.Collections.Generic;
using Xunit;

namespace TiYf.Engine.Tests
{
    public class DailyMonitorSummaryTests
    {
        [Fact]
        public void AppendsConfigIdWhenPresent()
        {
            var line = BuildSummary("demo-oanda-v1");
            Assert.EndsWith("config_id=demo-oanda-v1", line);
            Assert.Contains("config_id=demo-oanda-v1", line);
        }

        [Fact]
        public void OmitsConfigIdWhenMissing()
        {
            var line = BuildSummary(string.Empty);
            Assert.DoesNotContain("config_id=", line);
        }

        private static string BuildSummary(string configId)
        {
            var fields = new List<string>
            {
                "daily-monitor:",
                "adapter=oanda-demo",
                "connected=True"
            };

            if (!string.IsNullOrWhiteSpace(configId))
            {
                fields.Add($"config_id={configId}");
            }

            return string.Join(' ', fields);
        }
    }
}
