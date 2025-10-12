using TiYf.Engine.Core;

namespace TiYf.Engine.Tests;

public class BarBuilderTests
{
    [Fact]
    public void ProducesBarAtMinuteBoundary()
    {
        var builder = new IntervalBarBuilder(BarInterval.OneMinute);
        var inst = new InstrumentId("I1");
        var t0 = new DateTime(2025, 10, 5, 12, 0, 5, DateTimeKind.Utc);
        var t1 = t0.AddSeconds(30);
        var t2 = t0.AddMinutes(1); // boundary
        Bar? b0 = builder.OnTick(new PriceTick(inst, t0, 100m, 1m));
        Assert.Null(b0);
        Bar? b1 = builder.OnTick(new PriceTick(inst, t1, 102m, 2m));
        Assert.Null(b1);
        Bar? flushed = builder.OnTick(new PriceTick(inst, t2, 102m, 0.5m));
        Assert.NotNull(flushed);
        Assert.Equal(100m, flushed!.Open);
        Assert.Equal(102m, flushed.High);
        Assert.Equal(100m, flushed.Low);
        Assert.Equal(102m, flushed.Close);
        Assert.Equal(3m, flushed.Volume);
    }

    [Fact]
    public void ProducesHourlyBar()
    {
        var builder = new IntervalBarBuilder(BarInterval.OneHour);
        var inst = new InstrumentId("I1");
        var baseTime = new DateTime(2025, 10, 5, 9, 15, 0, DateTimeKind.Utc);
        builder.OnTick(new PriceTick(inst, baseTime, 100m, 1m));
        builder.OnTick(new PriceTick(inst, baseTime.AddMinutes(30), 105m, 2m));
        var flushed = builder.OnTick(new PriceTick(inst, baseTime.AddHours(1), 104m, 1m));
        Assert.NotNull(flushed);
        Assert.Equal(100m, flushed!.Open);
        Assert.Equal(105m, flushed.High);
        Assert.Equal(100m, flushed.Low);
        Assert.Equal(105m, flushed.Close);
        Assert.Equal(3m, flushed.Volume);
    }

    [Fact]
    public void DailyAlignment()
    {
        var builder = new IntervalBarBuilder(BarInterval.OneDay);
        var inst = new InstrumentId("I1");
        var day = new DateTime(2025, 10, 5, 10, 0, 0, DateTimeKind.Utc);
        builder.OnTick(new PriceTick(inst, day.AddHours(1), 10m, 1m));
        builder.OnTick(new PriceTick(inst, day.AddHours(5), 12m, 2m));
        var flushed = builder.OnTick(new PriceTick(inst, day.AddDays(1), 11m, 3m));
        Assert.NotNull(flushed);
        Assert.Equal(10m, flushed!.Open);
        Assert.Equal(12m, flushed.High);
        Assert.Equal(10m, flushed.Low);
        Assert.Equal(12m, flushed.Close);
        Assert.Equal(3m, flushed.Volume);
    }
}