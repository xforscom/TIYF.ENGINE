using System.Text;
using System.Text.Json;
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
    /// <summary>
    /// Compute a parity hash that intentionally ignores:
    ///  - Root level sentimentConfig subtree
    ///  - featureFlags.sentiment toggle
    ///  - featureFlags.riskProbe toggle
    /// without mutating or reparsing the JSON text (which can throw when duplicate keys exist
    /// in test-mutated configs). We stream the original JsonDocument to a canonical minified
    /// JSON buffer applying filters on the fly. If any error occurs we fall back to a stable
    /// sentinel so the engine does not crash (tests will surface the fallback if needed).
    /// </summary>
    public static string Compute(JsonDocument raw)
    {
        try
        {
            if (raw.RootElement.ValueKind != JsonValueKind.Object)
                return "PARITY_HASH_UNSUPPORTED"; // unexpected shape

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { SkipValidation = true }))
            {
                writer.WriteStartObject();
                foreach (var prop in raw.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("sentimentConfig"))
                        continue; // skip entire subtree

                    if (prop.NameEquals("featureFlags") && prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        writer.WritePropertyName(prop.Name);
                        writer.WriteStartObject();
                        foreach (var ff in prop.Value.EnumerateObject())
                        {
                            if (ff.NameEquals("sentiment") || ff.NameEquals("riskProbe"))
                                continue; // omit ephemeral toggles
                            ff.WriteTo(writer);
                        }
                        writer.WriteEndObject();
                        continue;
                    }

                    // default: copy as-is
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
                writer.Flush();
            }

            using var sha = SHA256.Create();
            var bytes = ms.ToArray();
            return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("X2")));
        }
        catch (Exception ex)
        {
            // Swallow & degrade – engine must not crash due to parity hash computation.
            Console.Error.WriteLine($"ParityConfigHash error: {ex.Message}");
            return "PARITY_HASH_ERROR";
        }
    }
}
