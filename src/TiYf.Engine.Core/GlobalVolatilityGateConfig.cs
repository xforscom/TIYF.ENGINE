using System;
using System.Collections.Generic;

namespace TiYf.Engine.Core;

public sealed record GlobalVolatilityComponentConfig(string Name, decimal Weight);

public sealed record GlobalVolatilityGateConfig(
    string EnabledMode,
    decimal EntryThreshold,
    decimal EwmaAlpha,
    IReadOnlyList<GlobalVolatilityComponentConfig> Components)
{
    private static readonly GlobalVolatilityComponentConfig[] DefaultComponents =
    {
        new("fx_atr_percentile", 0.6m),
        new("risk_proxy_z", 0.4m)
    };

    public static GlobalVolatilityGateConfig Disabled { get; } = new(
        "disabled",
        0m,
        0.3m,
        DefaultComponents);

    public bool IsEnabled =>
        !string.Equals(EnabledMode, "disabled", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<GlobalVolatilityComponentConfig> EffectiveComponents =>
        Components is { Count: > 0 } ? Components : DefaultComponents;
}
