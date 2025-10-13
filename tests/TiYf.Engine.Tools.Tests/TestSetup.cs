using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace TiYf.Engine.Tools.Tests;

internal static class TestSetup
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        Directory.SetCurrentDirectory(repoRoot);
    }
}
