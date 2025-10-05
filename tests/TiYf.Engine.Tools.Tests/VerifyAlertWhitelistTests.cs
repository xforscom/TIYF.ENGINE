using System.Text;
using TiYf.Engine.Tools;
using Xunit;

namespace TiYf.Engine.Tools.Tests;

public class VerifyAlertWhitelistTests
{
    [Fact]
    public void Verify_Accepts_Alert_Events()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "alert-whitelist-"+Guid.NewGuid().ToString("N")+".csv");
        var sb = new StringBuilder();
        sb.AppendLine("schema_version=1.1.0,config_hash=ABC123");
        sb.AppendLine("sequence,utc_ts,event_type,payload_json");
        var now = DateTime.UtcNow.ToString("O");
        var payload = "{\"InstrumentId\":\"EURUSD\",\"DecisionId\":\"D1\",\"Observed\":7.0,\"Cap\":5.0}";
        sb.AppendLine($"1,{now},ALERT_BLOCK_LEVERAGE,{payload}");
        File.WriteAllText(tmp, sb.ToString());
        var res = VerifyEngine.Run(tmp, new VerifyOptions(10,false,false));
        Assert.Equal(0, res.ExitCode);
    }
}
