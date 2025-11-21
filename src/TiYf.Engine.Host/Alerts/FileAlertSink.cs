using System;
using System.IO;

namespace TiYf.Engine.Host.Alerts;

public sealed class FileAlertSink : IAlertSink
{
    private readonly string _path;
    private readonly object _sync = new();

    public FileAlertSink(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void Enqueue(AlertRecord alert)
    {
        var line = $"{alert.OccurredUtc:o} {alert.Category}/{alert.Severity} {alert.Summary}";
        lock (_sync)
        {
            File.AppendAllLines(_path, new[] { line });
        }
    }
}
