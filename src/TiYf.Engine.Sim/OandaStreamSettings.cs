using System.Text.Json;

namespace TiYf.Engine.Sim;

public sealed record OandaStreamSettings(
    bool Enable,
    Uri BaseUri,
    string PricingEndpoint,
    IReadOnlyList<string> Instruments,
    TimeSpan HeartbeatTimeout,
    TimeSpan MaxBackoff,
    string FeedMode,
    string? ReplayTicksFile)
{
    public static OandaStreamSettings? FromJson(JsonElement adapterNode, JsonElement rootConfig, string adapterType)
    {
        if (!adapterNode.TryGetProperty("settings", out var settingsNode) || settingsNode.ValueKind != JsonValueKind.Object)
        {
            settingsNode = adapterNode;
        }

        if (!settingsNode.TryGetProperty("stream", out var streamNode) || streamNode.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        bool enable = streamNode.TryGetProperty("enable", out var enableNode) && enableNode.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(enableNode.GetString(), out var parsed) && parsed,
            _ => false
        };

        var lower = adapterType?.ToLowerInvariant() ?? "oanda-demo";
        var defaultBase = lower == "oanda-live"
            ? new Uri("https://stream-fxtrade.oanda.com/v3/", UriKind.Absolute)
            : new Uri("https://stream-fxpractice.oanda.com/v3/", UriKind.Absolute);

        static Uri ResolveUri(JsonElement node, string propertyName, Uri fallback)
        {
            if (node.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var text = prop.GetString();
                if (!string.IsNullOrWhiteSpace(text) && Uri.TryCreate(text, UriKind.Absolute, out var uri))
                {
                    return uri;
                }
            }
            return fallback;
        }

        static TimeSpan ResolveTimeSpan(JsonElement node, string propertyName, TimeSpan fallback)
        {
            if (node.TryGetProperty(propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var seconds))
                {
                    if (seconds <= 0) return fallback;
                    return TimeSpan.FromSeconds(seconds);
                }
                if (prop.ValueKind == JsonValueKind.String)
                {
                    var text = prop.GetString();
                    if (double.TryParse(text, out var secs) && secs > 0)
                    {
                        return TimeSpan.FromSeconds(secs);
                    }
                    if (TimeSpan.TryParse(text, out var span) && span > TimeSpan.Zero)
                    {
                        return span;
                    }
                }
            }
            return fallback;
        }

        var baseUri = ResolveUri(streamNode, "baseUrl", defaultBase);
        var pricingEndpoint = streamNode.TryGetProperty("pricingEndpoint", out var endpointNode) && endpointNode.ValueKind == JsonValueKind.String
            ? endpointNode.GetString() ?? "/accounts/{accountId}/pricing/stream"
            : "/accounts/{accountId}/pricing/stream";

        var instruments = ResolveInstruments(streamNode, rootConfig);
        var heartbeat = ResolveTimeSpan(streamNode, "heartbeatTimeoutSeconds", TimeSpan.FromSeconds(15));
        var maxBackoff = ResolveTimeSpan(streamNode, "maxBackoffSeconds", TimeSpan.FromSeconds(10));
        if (maxBackoff > TimeSpan.FromSeconds(10))
        {
            maxBackoff = TimeSpan.FromSeconds(10);
        }

        var feedMode = streamNode.TryGetProperty("feedMode", out var modeNode) && modeNode.ValueKind == JsonValueKind.String
            ? (modeNode.GetString() ?? "live")
            : "live";
        feedMode = feedMode.Trim().ToLowerInvariant();
        if (feedMode is not ("live" or "replay"))
        {
            feedMode = "live";
        }

        string? replayTicks = null;
        if (streamNode.TryGetProperty("replayTicksFile", out var replayNode) && replayNode.ValueKind == JsonValueKind.String)
        {
            replayTicks = replayNode.GetString();
        }

        return new OandaStreamSettings(enable, baseUri, pricingEndpoint, instruments, heartbeat, maxBackoff, feedMode, replayTicks);
    }

    private static IReadOnlyList<string> ResolveInstruments(JsonElement streamNode, JsonElement root)
    {
        List<string> list = new();
        if (streamNode.TryGetProperty("instruments", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var symbol = item.GetString();
                    if (!string.IsNullOrWhiteSpace(symbol))
                    {
                        list.Add(NormalizeInstrument(symbol));
                    }
                }
            }
        }

        if (list.Count == 0 && root.TryGetProperty("universe", out var universeNode) && universeNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in universeNode.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var symbol = item.GetString();
                    if (!string.IsNullOrWhiteSpace(symbol))
                    {
                        list.Add(NormalizeInstrument(symbol));
                    }
                }
            }
        }

        if (list.Count == 0)
        {
            list.Add("EUR_USD");
        }

        return list;
    }

    private static string NormalizeInstrument(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var trimmed = raw.Trim().ToUpperInvariant();
        if (trimmed.Contains('_')) return trimmed;
        if (trimmed.Length == 7 && trimmed[3] == '/')
        {
            trimmed = trimmed.Replace("/", "");
        }
        if (trimmed.Length == 6)
        {
            return $"{trimmed[..3]}_{trimmed[3..]}";
        }
        return trimmed;
    }
}
