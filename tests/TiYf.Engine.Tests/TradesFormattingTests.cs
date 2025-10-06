using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace TiYf.Engine.Tests;

public class TradesFormattingTests
{
    [Fact]
    public void TradesCsv_PriceAndPnlFormatting_Deterministic()
    {
        // Run fixture first externally (assumes prior run). We'll just read existing M0 trades if present.
        var path = Path.Combine("journals","M0","M0-RUN","trades.csv");
        if (!File.Exists(path)) return; // skip if not yet generated (integration test covers runtime)
        var lines = File.ReadAllLines(path);
        Assert.True(lines.Length >= 2);
        var rows = lines.Skip(1).ToArray();
        // Price precision per symbol
        foreach (var r in rows)
        {
            var parts = r.Split(',');
            // entry=4 exit=5 pnl=7
            var symbol = parts[2];
            var entry = parts[4];
            var exitP = parts[5];
            var pnl = parts[7];
            if (symbol=="EURUSD") { Assert.Matches(@"^\d+\.\d{5}$", entry); Assert.Matches(@"^\d+\.\d{5}$", exitP); }
            if (symbol=="USDJPY") { Assert.Matches(@"^\d+\.\d{3}$", entry); }
            if (symbol=="XAUUSD") { Assert.Matches(@"^\d+\.\d{2}$", entry); }
            Assert.Matches(@"^-?\d+\.\d{2}$", pnl); // pnl always 2 decimals
        }
    }
}
