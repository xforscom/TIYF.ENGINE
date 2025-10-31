using System;
using System.Reflection;
using TiYf.Engine.Host;
using Xunit;

namespace TiYf.Engine.Tests;

public class EngineLoopServiceAlignmentTests
{
    [Fact]
    public void AlignToMinute_TruncatesToUtcMinute()
    {
        var method = typeof(EngineLoopService).GetMethod("AlignToMinute", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var input = new DateTime(2024, 6, 10, 14, 37, 45, DateTimeKind.Utc).AddMilliseconds(512);
        var aligned = (DateTime)method!.Invoke(null, new object[] { input })!;
        Assert.Equal(new DateTime(2024, 6, 10, 14, 37, 0, DateTimeKind.Utc), aligned);
        Assert.Equal(DateTimeKind.Utc, aligned.Kind);
    }
}
