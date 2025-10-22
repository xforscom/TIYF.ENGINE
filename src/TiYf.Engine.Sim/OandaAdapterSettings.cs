using System.Text.Json;

namespace TiYf.Engine.Sim;

public sealed record OandaAdapterSettings(
    string Mode,
    Uri BaseUri,
    string AccountId,
    string AccessToken,
    bool UseMock,
    long MaxOrderUnits,
    TimeSpan RequestTimeout,
    TimeSpan RetryInitialDelay,
    TimeSpan RetryMaxDelay,
    int RetryMaxAttempts,
    string HandshakeEndpoint,
    string OrderEndpoint)
{
    public static OandaAdapterSettings FromJson(JsonElement adapterNode, string adapterType)
    {
        var lower = adapterType?.ToLowerInvariant() ?? "oanda-demo";
        var defaults = DefaultsFor(lower);
        var cfgNode = adapterNode.TryGetProperty("settings", out var settingsNode) && settingsNode.ValueKind == JsonValueKind.Object
            ? settingsNode
            : adapterNode;

        static string ResolveString(JsonElement node, string propertyName, string envVar, string fallback = "")
        {
            if (node.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString() ?? string.Empty;
                if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
                {
                    var env = Environment.GetEnvironmentVariable(value.Substring(4));
                    return string.IsNullOrWhiteSpace(env) ? fallback : env!;
                }
                return value;
            }
            if (!string.IsNullOrWhiteSpace(envVar))
            {
                var env = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrWhiteSpace(env)) return env!;
            }
            return fallback;
        }

        static bool ResolveBool(JsonElement node, string propertyName, bool fallback)
        {
            if (node.TryGetProperty(propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True) return true;
                if (prop.ValueKind == JsonValueKind.False) return false;
                if (prop.ValueKind == JsonValueKind.String && bool.TryParse(prop.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
            return fallback;
        }

        static long ResolveLong(JsonElement node, string propertyName, long fallback)
        {
            if (node.TryGetProperty(propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var value)) return value;
                if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var parsed)) return parsed;
            }
            return fallback;
        }

        static TimeSpan ResolveTimeSpan(JsonElement node, string propertyName, TimeSpan fallback)
        {
            if (node.TryGetProperty(propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var seconds))
                {
                    return TimeSpan.FromSeconds(seconds);
                }
                if (prop.ValueKind == JsonValueKind.String)
                {
                    var text = prop.GetString();
                    if (double.TryParse(text, out var secs)) return TimeSpan.FromSeconds(secs);
                    if (TimeSpan.TryParse(text, out var span)) return span;
                }
            }
            return fallback;
        }

        var baseUrl = ResolveString(cfgNode, "baseUrl", string.Empty, defaults.BaseUri.ToString());
        var accessToken = ResolveString(cfgNode, "accessToken", lower == "oanda-live" ? "OANDA_LIVE_TOKEN" : "OANDA_PRACTICE_TOKEN", defaults.AccessToken);
        var accountId = ResolveString(cfgNode, "accountId", lower == "oanda-live" ? "OANDA_LIVE_ACCOUNT_ID" : "OANDA_PRACTICE_ACCOUNT_ID", defaults.AccountId);

        var useMockFallback = string.IsNullOrWhiteSpace(accessToken) ? true : defaults.UseMock;
        var useMock = ResolveBool(cfgNode, "useMock", useMockFallback);
        var maxUnits = ResolveLong(cfgNode, "maxOrderUnits", defaults.MaxOrderUnits);
        var timeout = ResolveTimeSpan(cfgNode, "requestTimeoutSeconds", defaults.RequestTimeout);
        var retryInitial = ResolveTimeSpan(cfgNode, "retryInitialDelaySeconds", defaults.RetryInitialDelay);
        var retryMax = ResolveTimeSpan(cfgNode, "retryMaxDelaySeconds", defaults.RetryMaxDelay);
        var retryAttempts = (int)ResolveLong(cfgNode, "retryMaxAttempts", defaults.RetryMaxAttempts);
        var handshakeEndpoint = ResolveString(cfgNode, "handshakeEndpoint", string.Empty, defaults.HandshakeEndpoint);
        var orderEndpoint = ResolveString(cfgNode, "orderEndpoint", string.Empty, defaults.OrderEndpoint);

        return new OandaAdapterSettings(
            lower,
            new Uri(baseUrl, UriKind.RelativeOrAbsolute),
            string.IsNullOrWhiteSpace(accountId) ? defaults.AccountId : accountId,
            accessToken,
            useMock,
            maxUnits,
            timeout <= TimeSpan.Zero ? defaults.RequestTimeout : timeout,
            retryInitial <= TimeSpan.Zero ? defaults.RetryInitialDelay : retryInitial,
            retryMax <= TimeSpan.Zero ? defaults.RetryMaxDelay : retryMax,
            retryAttempts <= 0 ? defaults.RetryMaxAttempts : retryAttempts,
            string.IsNullOrWhiteSpace(handshakeEndpoint) ? defaults.HandshakeEndpoint : handshakeEndpoint,
            string.IsNullOrWhiteSpace(orderEndpoint) ? defaults.OrderEndpoint : orderEndpoint);
    }

    private static OandaAdapterSettings DefaultsFor(string mode)
    {
        var live = string.Equals(mode, "oanda-live", StringComparison.OrdinalIgnoreCase);
        var baseUri = live
            ? new Uri("https://api-fxtrade.oanda.com/v3/")
            : new Uri("https://api-fxpractice.oanda.com/v3/");

        return new OandaAdapterSettings(
            mode,
            baseUri,
            AccountId: string.Empty,
            AccessToken: string.Empty,
            UseMock: false,
            MaxOrderUnits: 100_000,
            RequestTimeout: TimeSpan.FromSeconds(10),
            RetryInitialDelay: TimeSpan.FromMilliseconds(200),
            RetryMaxDelay: TimeSpan.FromSeconds(2),
            RetryMaxAttempts: 5,
            HandshakeEndpoint: "/accounts/{accountId}/summary",
            OrderEndpoint: "/accounts/{accountId}/orders");
    }
}
