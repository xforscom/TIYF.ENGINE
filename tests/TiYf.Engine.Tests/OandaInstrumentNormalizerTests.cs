using TiYf.Engine.Sim;
using Xunit;

namespace TiYf.Engine.Tests;

public class OandaInstrumentNormalizerTests
{
    [Theory]
    [InlineData("EUR_USD", "EURUSD")]
    [InlineData("eur/usd", "EURUSD")]
    [InlineData(" eurusd ", "EURUSD")]
    [InlineData("GBPJPY", "GBPJPY")]
    [InlineData("btc_usd", "BTCUSD")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void ToCanonical_ReturnsExpected(string? input, string? expected)
    {
        var actual = OandaInstrumentNormalizer.ToCanonical(input);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("EURUSD", "EUR_USD")]
    [InlineData("eur/usd", "EUR_USD")]
    [InlineData(" eur_usd ", "EUR_USD")]
    [InlineData("GBPJPY", "GBP_JPY")]
    [InlineData("BTC_USD", "BTC_USD")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void ToApiSymbol_ReturnsExpected(string? input, string? expected)
    {
        var actual = OandaInstrumentNormalizer.ToApiSymbol(input);
        Assert.Equal(expected, actual);
    }
}
