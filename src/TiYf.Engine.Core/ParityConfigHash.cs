using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace TiYf.Engine.Core;

/// <summary>
/// Computes a normalized configuration hash that intentionally ignores ephemeral
/// sentiment-related feature flag fields so that OFF vs SHADOW modes share the same
/// identity for parity (trades) invariants. Active mode may still share this hash; divergence
/// is enforced via INFO_SENTIMENT_APPLIED_V1 events and adjusted units, not the hash itself.
/// </summary>
public static class ParityConfigHash
{
    public static string Compute(JsonDocument raw)
    {
        // Parse into JsonNode to allow structural edits.
        JsonNode? node = JsonNode.Parse(raw.RootElement.GetRawText());
        if (node is JsonObject obj)
        {
            // Remove sentimentConfig subtree entirely (window, sigma, etc.).
            obj.Remove("sentimentConfig");
            // Normalize featureFlags.sentiment (remove property) so differing modes hash identically.
            if (obj.TryGetPropertyValue("featureFlags", out var ffNode) && ffNode is JsonObject ffObj)
            {
                ffObj.Remove("sentiment");
                // riskProbe is forced disabled in matrix runs for determinism; remove to avoid future drift.
                ffObj.Remove("riskProbe");
            }
        }
        // Canonical (minified) serialization – property order preserved as inserted by System.Text.Json
        // which is stable for the transformed structure.
        var canonical = node!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(canonical);
        return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("X2")));
    }
}
