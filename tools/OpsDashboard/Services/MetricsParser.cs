using System.Globalization;

namespace OpsDashboard.Services;

public sealed class MetricsParser
{
    public Dictionary<string, List<MetricSample>> Parse(string? raw)
    {
        var result = new Dictionary<string, List<MetricSample>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var parsed = TryParseLine(trimmed);
            if (parsed is null)
            {
                continue;
            }

            if (!result.TryGetValue(parsed.Value.Name, out var list))
            {
                list = new List<MetricSample>();
                result[parsed.Value.Name] = list;
            }
            list.Add(new MetricSample(parsed.Value.Labels, parsed.Value.Value));
        }

        return result;
    }

    private static (string Name, Dictionary<string, string> Labels, string Value)? TryParseLine(string line)
    {
        try
        {
            var name = line;
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            var valuePart = string.Empty;

            var braceIndex = line.IndexOf('{');
            var spaceIndex = line.LastIndexOf(' ');
            if (spaceIndex < 0)
            {
                return null;
            }
            if (braceIndex > 0)
            {
                name = line.Substring(0, braceIndex);
                var closingBrace = line.IndexOf('}', braceIndex + 1);
                if (closingBrace > braceIndex)
                {
                    var labelsSegment = line.Substring(braceIndex + 1, closingBrace - braceIndex - 1);
                    labels = ParseLabels(labelsSegment);
                    valuePart = line[(closingBrace + 1)..].Trim();
                }
            }
            else
            {
                name = line.Substring(0, spaceIndex);
                valuePart = line[(spaceIndex + 1)..].Trim();
            }

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(valuePart))
            {
                return null;
            }

            return (name, labels, valuePart);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParseLabels(string labelsSegment)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(labelsSegment))
        {
            return labels;
        }

        var parts = labelsSegment.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = part.Substring(0, eq).Trim();
            var rawValue = part[(eq + 1)..].Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(key))
            {
                labels[key] = rawValue;
            }
        }
        return labels;
    }
}

public sealed record MetricSample(Dictionary<string, string> Labels, string Value);
