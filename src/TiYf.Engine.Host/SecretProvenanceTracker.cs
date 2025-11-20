using System;
using System.Collections.Generic;
using System.Linq;

namespace TiYf.Engine.Host;

internal sealed class SecretProvenanceTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, HashSet<string>> _sources = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string? integration, string? source)
    {
        var normalizedIntegration = NormalizeIntegration(integration);
        var normalizedSource = NormalizeSource(source);
        if (string.IsNullOrWhiteSpace(normalizedIntegration))
        {
            return;
        }

        lock (_sync)
        {
            if (!_sources.TryGetValue(normalizedIntegration, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _sources[normalizedIntegration] = set;
            }
            set.Add(normalizedSource);
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> CreateSnapshot()
    {
        lock (_sync)
        {
            return _sources.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyCollection<string>)kvp.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeIntegration(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unknown";
        }
        return name.Trim().ToLowerInvariant().Replace('-', '_');
    }

    private static string NormalizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "unknown";
        }
        return source.Trim().ToLowerInvariant() switch
        {
            "env" => "env",
            "config" => "config",
            "config_ignored" => "config",
            "missing" => "missing",
            "default" => "default",
            _ => "unknown"
        };
    }
}
