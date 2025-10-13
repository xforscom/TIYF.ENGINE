using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace TiYf.Engine.Tools.Tests;

internal static class TestSetup
{
#pragma warning disable CA2255 // Module initializers are intentional for test directory normalization
    [ModuleInitializer]
    public static void Initialize()
    {
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        Directory.SetCurrentDirectory(repoRoot);
    }
#pragma warning restore CA2255
}
