using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiYf.Engine.Core;

public enum RiskMode { Off, Shadow, Active }

public static class RiskParsing
{
    public static RiskMode ParseRiskMode(JsonNode? featureFlags)
    {
        if (featureFlags is null) return RiskMode.Off;
        try
        {
            if (featureFlags is JsonObject obj)
            {
                if (obj.TryGetPropertyValue("risk", out var val) && val is JsonValue jv && jv.TryGetValue<string>(out var s))
                    return Map(s);
                if (obj.TryGetPropertyValue("riskMode", out var rm) && rm is JsonValue jv2 && jv2.TryGetValue<string>(out var s2))
                    return Map(s2);
            }
        }
        catch { }
        return RiskMode.Off;
    }

    private static RiskMode Map(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return RiskMode.Off;
        raw = raw.Trim().ToLowerInvariant();
        return raw switch
        {
            "off" or "disabled" or "none" => RiskMode.Off,
            "shadow" or "monitor" => RiskMode.Shadow,
            "active" or "enforce" => RiskMode.Active,
            _ => RiskMode.Off
        };
    }
}