using System.Text.Json;

namespace TiYf.Engine.Core;

public static class GvrsGateConfigHelper
{
    public static bool ResolveGvrsGateEnabled(JsonDocument raw)
    {
        if (raw.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (raw.RootElement.TryGetProperty("gvrs_gate", out var gateNode) &&
            gateNode.ValueKind == JsonValueKind.Object &&
            gateNode.TryGetProperty("enabled", out var enabledNode) &&
            (enabledNode.ValueKind == JsonValueKind.True || enabledNode.ValueKind == JsonValueKind.False))
        {
            return enabledNode.GetBoolean();
        }

        return false;
    }
}
