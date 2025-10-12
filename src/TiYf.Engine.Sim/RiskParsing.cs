using System.Text.Json.Nodes;
using System.Text.Json;

namespace TiYf.Engine.Sim;

// Shim delegating to Core RiskParsing to preserve legacy namespace references.
public enum RiskMode { Off, Shadow, Active }

public static class RiskParsing
{
    public static RiskMode ParseRiskMode(JsonNode? node)
    {
        try
        {
            if (node is null) return RiskMode.Off;
            if (node is not JsonObject obj) return RiskMode.Off;
            if (!obj.TryGetPropertyValue("featureFlags", out var ff) || ff is null) return RiskMode.Off;
            var riskProp = ff?["risk"]?.ToString();
            if (string.IsNullOrWhiteSpace(riskProp)) return RiskMode.Off;
            return Map(riskProp);
        }
        catch { return RiskMode.Off; }
    }

    public static RiskMode ParseRiskMode(JsonElement element)
    {
        try
        {
            if (element.ValueKind != JsonValueKind.Object) return RiskMode.Off;
            if (!element.TryGetProperty("featureFlags", out var ff) || ff.ValueKind != JsonValueKind.Object) return RiskMode.Off;
            if (!ff.TryGetProperty("risk", out var r) || r.ValueKind != JsonValueKind.String) return RiskMode.Off;
            return Map(r.GetString() ?? string.Empty);
        }
        catch { return RiskMode.Off; }
    }

    private static RiskMode Map(string raw)
    {
        if (raw.Equals("active", StringComparison.OrdinalIgnoreCase)) return RiskMode.Active;
        if (raw.Equals("shadow", StringComparison.OrdinalIgnoreCase)) return RiskMode.Shadow;
        if (raw.Equals("off", StringComparison.OrdinalIgnoreCase) || raw.Equals("disabled", StringComparison.OrdinalIgnoreCase) || raw.Equals("none", StringComparison.OrdinalIgnoreCase)) return RiskMode.Off;
        return RiskMode.Off;
    }
}