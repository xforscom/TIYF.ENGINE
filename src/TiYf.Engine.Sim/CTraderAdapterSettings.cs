using System.Text.Json;

namespace TiYf.Engine.Sim;

public sealed record CTraderAdapterSettings(
    string Mode,
    Uri BaseUri,
    Uri TokenUri,
    string ApplicationId,
    string ClientSecret,
    string ClientId,
    string AccessToken,
    string RefreshToken,
    string AccountId,
    string Broker,
    bool UseMock,
    long MaxOrderUnits,
    decimal MaxNotional,
    TimeSpan RequestTimeout,
    TimeSpan RetryInitialDelay,
    TimeSpan RetryMaxDelay,
    int RetryMaxAttempts,
    string HandshakeEndpoint,
    string OrderEndpoint)
{
    public static CTraderAdapterSettings FromJson(JsonElement adapterNode, string adapterType)
    {
        var lower = adapterType?.ToLowerInvariant() ?? "ctrader-demo";
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
                if (prop.ValueKind == JsonValueKind.String)
                {
                    if (bool.TryParse(prop.GetString(), out var parsed)) return parsed;
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

        static decimal ResolveDecimal(JsonElement node, string propertyName, decimal fallback)
        {
            if (node.TryGetProperty(propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var value)) return value;
                if (prop.ValueKind == JsonValueKind.String && decimal.TryParse(prop.GetString(), out var parsed)) return parsed;
            }
            return fallback;
        }

        static TimeSpan ResolveTimeSpan(JsonElement node, string propertyName, TimeSpan fallback)
        {
            if (node.TryGetProperty(propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var seconds)) return TimeSpan.FromSeconds(seconds);
                if (prop.ValueKind == JsonValueKind.String)
                {
                    var text = prop.GetString();
                    if (TimeSpan.TryParse(text, out var parsed)) return parsed;
                    if (double.TryParse(text, out var secs)) return TimeSpan.FromSeconds(secs);
                }
            }
            return fallback;
        }

        var baseUrl = ResolveString(cfgNode, "baseUrl", string.Empty, defaults.BaseUri.ToString());
        var tokenUrl = ResolveString(cfgNode, "tokenUrl", string.Empty, defaults.TokenUri.ToString());
        var appId = ResolveString(cfgNode, "applicationId", "CT_APP_ID", defaults.ApplicationId);
        var clientSecret = ResolveString(cfgNode, "clientSecret", "CT_APP_SECRET", defaults.ClientSecret);
        var clientId = ResolveString(cfgNode, "clientId", "CT_CLIENT_ID", string.IsNullOrWhiteSpace(defaults.ClientId) ? appId : defaults.ClientId);
        var accessToken = ResolveString(cfgNode, "accessToken", lower == "ctrader-demo" ? "CT_DEMO_OAUTH_TOKEN" : "CT_LIVE_OAUTH_TOKEN", defaults.AccessToken);
        var refreshToken = ResolveString(cfgNode, "refreshToken", lower == "ctrader-demo" ? "CT_DEMO_REFRESH_TOKEN" : "CT_LIVE_REFRESH_TOKEN", defaults.RefreshToken);
        var accountId = ResolveString(cfgNode, "accountId", lower == "ctrader-demo" ? "CT_DEMO_ACCOUNT_ID" : "CT_LIVE_ACCOUNT_ID", defaults.AccountId);
        var broker = ResolveString(cfgNode, "broker", lower == "ctrader-demo" ? "CT_DEMO_BROKER" : "CT_LIVE_BROKER", defaults.Broker);

        var useMockFallback = string.IsNullOrWhiteSpace(accessToken) ? true : defaults.UseMock;
        var useMock = ResolveBool(cfgNode, "useMock", useMockFallback);
        var maxUnits = ResolveLong(cfgNode, "maxOrderUnits", defaults.MaxOrderUnits);
        var maxNotional = ResolveDecimal(cfgNode, "maxNotional", defaults.MaxNotional);
        var timeout = ResolveTimeSpan(cfgNode, "requestTimeoutSeconds", defaults.RequestTimeout);
        var retryInitial = ResolveTimeSpan(cfgNode, "retryInitialDelaySeconds", defaults.RetryInitialDelay);
        var retryMax = ResolveTimeSpan(cfgNode, "retryMaxDelaySeconds", defaults.RetryMaxDelay);
        var retryAttempts = (int)ResolveLong(cfgNode, "retryMaxAttempts", defaults.RetryMaxAttempts);
        var handshakeEndpoint = ResolveString(cfgNode, "handshakeEndpoint", string.Empty, defaults.HandshakeEndpoint);
        var orderEndpoint = ResolveString(cfgNode, "orderEndpoint", string.Empty, defaults.OrderEndpoint);

        return new CTraderAdapterSettings(
            lower,
            new Uri(baseUrl, UriKind.RelativeOrAbsolute),
            new Uri(tokenUrl, UriKind.RelativeOrAbsolute),
            appId,
            clientSecret,
            clientId,
            accessToken,
            refreshToken,
            accountId,
            string.IsNullOrWhiteSpace(broker) ? defaults.Broker : broker,
            useMock,
            maxUnits,
            maxNotional,
            timeout <= TimeSpan.Zero ? defaults.RequestTimeout : timeout,
            retryInitial <= TimeSpan.Zero ? defaults.RetryInitialDelay : retryInitial,
            retryMax <= TimeSpan.Zero ? defaults.RetryMaxDelay : retryMax,
            retryAttempts <= 0 ? defaults.RetryMaxAttempts : retryAttempts,
            string.IsNullOrWhiteSpace(handshakeEndpoint) ? defaults.HandshakeEndpoint : handshakeEndpoint,
            string.IsNullOrWhiteSpace(orderEndpoint) ? defaults.OrderEndpoint : orderEndpoint);
    }

    private static CTraderAdapterSettings DefaultsFor(string mode)
    {
        var isDemo = !string.Equals(mode, "ctrader-live", StringComparison.OrdinalIgnoreCase);
        var baseUri = isDemo
            ? new Uri("https://api.spotware.com/openapi/ct-demo")
            : new Uri("https://api.spotware.com/openapi/ct-live");
        var tokenUri = new Uri("https://api.spotware.com/openapi/token");

        return new CTraderAdapterSettings(
            mode,
            baseUri,
            tokenUri,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            isDemo ? "Spotware" : "Spotware",
            UseMock: false,
            MaxOrderUnits: 100_000,
            MaxNotional: 1_000_000m,
            RequestTimeout: TimeSpan.FromSeconds(10),
            RetryInitialDelay: TimeSpan.FromSeconds(1),
            RetryMaxDelay: TimeSpan.FromSeconds(10),
            RetryMaxAttempts: 5,
            HandshakeEndpoint: "/connectivity",
            OrderEndpoint: "/orders");
    }
}
