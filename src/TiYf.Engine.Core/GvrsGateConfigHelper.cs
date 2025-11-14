using System.Text.Json;

namespace TiYf.Engine.Core;

public readonly record struct GvrsGateRuntimeConfig(bool Enabled, bool BlockOnVolatile);

public static class GvrsGateConfigHelper
{
    public static GvrsGateRuntimeConfig Resolve(JsonDocument raw)
    {
        if (raw.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new GvrsGateRuntimeConfig(false, false);
        }

        if (!raw.RootElement.TryGetProperty("gvrs_gate", out var gateNode) || gateNode.ValueKind != JsonValueKind.Object)
        {
            return new GvrsGateRuntimeConfig(false, false);
        }

        var enabled = gateNode.TryGetProperty("enabled", out var enabledNode) &&
                      (enabledNode.ValueKind == JsonValueKind.True || enabledNode.ValueKind == JsonValueKind.False) &&
                      enabledNode.GetBoolean();

        var block = gateNode.TryGetProperty("block_on_volatile", out var blockNode) &&
                    (blockNode.ValueKind == JsonValueKind.True || blockNode.ValueKind == JsonValueKind.False) &&
                    blockNode.GetBoolean();

        return new GvrsGateRuntimeConfig(enabled, block);
    }
}
