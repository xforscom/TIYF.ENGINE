using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using TiYf.Engine.Core.Infrastructure;

namespace TiYf.Engine.Core;

// =============================
// Risk Enforcement Domain Models
// =============================
public sealed record Proposal(
    string InstrumentId,
    decimal RequestedVolume,
    decimal NotionalValue,
    decimal UsedMarginAfterOrder,
    decimal PositionInitialRiskMoney,
    string DecisionId
);

public sealed record BasketSnapshot(
    IReadOnlyList<(string InstrumentId, decimal InitialRiskMoneyAccountCcy)> Positions
);

public sealed record RiskContext(
    decimal Equity,
    BasketSnapshot Snapshot,
    RiskConfig Config,
    IInstrumentCatalog Catalog,
    ICurrencyConversionService Fx
);

public sealed record RiskObservations(
    decimal ProjectedLeverage,
    decimal ProjectedMarginUsagePct,
    decimal PerPositionRiskPct,
    decimal BasketRiskPct,
    decimal? PostRoundProjectedLeverage,
    decimal? PostRoundProjectedMarginUsagePct
);

public sealed record EnforcementResult(
    bool Allowed,
    decimal? ScaledVolumeBeforeRound,
    decimal? ScaledVolumeRounded,
    RiskObservations Observed,
    IReadOnlyList<AlertEvent> Alerts
);

public sealed record AlertEvent(
    string EventType,
    string DecisionId,
    string InstrumentId,
    string Reason,
    decimal Observed,
    decimal Cap,
    decimal Equity,
    decimal NotionalValue,
    decimal UsedMarginAfterOrder,
    string SchemaVersion,
    string ConfigHash
);

public sealed record SessionWindowConfig(TimeSpan StartUtc, TimeSpan EndUtc);

public enum DailyCapAction
{
    Block,
    HalfSize
}

public sealed record DailyCapConfig(decimal? LossThreshold, decimal? GainThreshold, DailyCapAction Action);

public sealed record GlobalDrawdownConfig(decimal MaxDrawdown);

public sealed record NewsHttpSourceConfig(
    string? BaseUri,
    string? ApiKeyHeaderName = null,
    string? ApiKeyEnvVar = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyDictionary<string, string>? QueryParameters = null);

public sealed record NewsBlackoutConfig(
    bool Enabled,
    int MinutesBefore,
    int MinutesAfter,
    string? SourcePath,
    int PollSeconds = 60,
    string SourceType = "file",
    NewsHttpSourceConfig? Http = null);

public interface IRiskEnforcer
{
    EnforcementResult Enforce(Proposal p, RiskContext ctx);
}

public sealed class RiskConfig
{
    public decimal RealLeverageCap { get; init; } = 20.0m;
    public decimal MarginUsageCapPct { get; init; } = 80.0m;
    public decimal PerPositionRiskCapPct { get; init; } = 1.0m;
    public string BasketMode { get; init; } = "Base"; // Base|Quote|UsdProxy|InstrumentBucket
    public Dictionary<string, string> InstrumentBuckets { get; init; } = new();
    public bool EnableScaleToFit { get; init; } = false;
    public bool EnforcementEnabled { get; init; } = true;
    public decimal LotStep { get; init; } = 0.01m; // minimal lot step (vol rounding)
    // M4 risk guardrails additions (engine-level, outside enforcement sizing logic)
    public Dictionary<string, decimal>? MaxNetExposureBySymbol { get; init; } = null; // symbol -> absolute exposure cap
    public Dictionary<string, long>? MaxUnitsPerSymbol { get; init; } = null;
    public decimal? MaxRunDrawdownCCY { get; init; } = null; // run drawdown cap in account CCY
    public bool BlockOnBreach { get; init; } = true; // if true active mode suppresses trades on breach
    public bool EmitEvaluations { get; init; } = true; // control INFO_RISK_EVAL_V1 emission
    // Test/diagnostic hook: per-symbol map of evaluation ordinal -> force drawdown on that evaluation (1-based).
    // Example: { "EURUSD": 1 } => on first EURUSD risk eval, equity is dropped to exceed MaxRunDrawdownCCY.
    public Dictionary<string, int>? ForceDrawdownAfterEvals { get; init; } = null;
    public SessionWindowConfig? SessionWindow { get; init; }
    public DailyCapConfig? DailyCap { get; init; }
    public GlobalDrawdownConfig? GlobalDrawdown { get; init; }
    public NewsBlackoutConfig? NewsBlackout { get; init; }
    public PromotionConfig Promotion { get; init; } = PromotionConfig.Default;
    public GlobalVolatilityGateConfig GlobalVolatilityGate { get; init; } = GlobalVolatilityGateConfig.Disabled;
    public string? RiskConfigHash { get; init; }
    public string PromotionConfigHash => Promotion.ConfigHash;
}

public static class AlertTypes
{
    public const string BLOCK_LEVERAGE = "ALERT_BLOCK_LEVERAGE";
    public const string BLOCK_MARGIN = "ALERT_BLOCK_MARGIN";
    public const string BLOCK_RISK_CAP = "ALERT_BLOCK_RISK_CAP";
    public const string BLOCK_BASKET = "ALERT_BLOCK_BASKET";
    public const string INFO_SCALE = "INFO_SCALE_TO_FIT";
}

// Currency conversion abstraction (simple for now, existing placeholder) - reused but formalize
public interface ICurrencyConversionService
{
    decimal Convert(Currency from, Currency to, decimal amount);
}

public sealed class PassthroughFx : ICurrencyConversionService
{
    public decimal Convert(Currency from, Currency to, decimal amount) => amount; // 1:1
}

public sealed class RiskEnforcer : IRiskEnforcer
{
    private readonly IRiskFormulas _formulas;
    private readonly IBasketRiskAggregator _basketAgg;
    private readonly string _schemaVersion;
    private readonly string _configHash;

    public RiskEnforcer(IRiskFormulas formulas, IBasketRiskAggregator basketAgg, string schemaVersion, string configHash)
    {
        _formulas = formulas; _basketAgg = basketAgg; _schemaVersion = schemaVersion; _configHash = configHash;
    }

    public EnforcementResult Enforce(Proposal p, RiskContext ctx)
    {
        // Pre metrics
        var projectedLeverage = SafeDiv(p.NotionalValue, ctx.Equity);
        var projectedMarginUsagePct = SafeDiv(p.UsedMarginAfterOrder, ctx.Equity) * 100m;
        var perPositionRiskPct = SafeDiv(p.PositionInitialRiskMoney, ctx.Equity) * 100m;
        // Build basket risk positions (convert to account ccy through ctx.Fx) – assume USD for now
        var basketPositions = ctx.Snapshot.Positions
            .Select(pp => new PositionInitialRisk(new InstrumentId(pp.InstrumentId), pp.InitialRiskMoneyAccountCcy, new Currency("USD")));
        var basketRiskPct = _basketAgg.ComputeBasketRiskPct(basketPositions, ParseBasketMode(ctx.Config.BasketMode), new Currency("USD"), ctx.Equity, ctx.Config.InstrumentBuckets);

        var observations = new RiskObservations(projectedLeverage, projectedMarginUsagePct, perPositionRiskPct, basketRiskPct, null, null);

        if (!ctx.Config.EnforcementEnabled)
        {
            return new EnforcementResult(true, null, null, observations, Array.Empty<AlertEvent>());
        }

        bool breachLeverage = projectedLeverage > ctx.Config.RealLeverageCap;
        bool breachMargin = projectedMarginUsagePct > ctx.Config.MarginUsageCapPct;
        bool breachPerPos = perPositionRiskPct > ctx.Config.PerPositionRiskCapPct;
        bool breachBasket = basketRiskPct > ctx.Config.PerPositionRiskCapPct; // reuse per-position cap for now
        if (!(breachLeverage || breachMargin || breachPerPos || breachBasket))
        {
            return new EnforcementResult(true, null, null, observations, Array.Empty<AlertEvent>());
        }

        if (!ctx.Config.EnableScaleToFit)
        {
            var alerts = BuildBlockAlerts(p, ctx, breachLeverage, breachMargin, breachPerPos, breachBasket, observations);
            return new EnforcementResult(false, null, null, observations, alerts);
        }

        // Scaling logic
        decimal sL = breachLeverage ? ctx.Config.RealLeverageCap / projectedLeverage : 1m;
        decimal sM = breachMargin ? ctx.Config.MarginUsageCapPct / projectedMarginUsagePct : 1m;
        decimal sP = breachPerPos ? ctx.Config.PerPositionRiskCapPct / perPositionRiskPct : 1m;
        decimal sB = breachBasket ? ctx.Config.PerPositionRiskCapPct / basketRiskPct : 1m;
        var Smax = Min(1m, sL, sM, sP, sB);
        if (Smax <= 0m)
        {
            var alerts = BuildBlockAlerts(p, ctx, breachLeverage, breachMargin, breachPerPos, breachBasket, observations);
            return new EnforcementResult(false, null, null, observations, alerts);
        }
        var scaledBefore = p.RequestedVolume * Smax;
        var scaledRounded = RoundDownToStep(scaledBefore, ctx.Config.LotStep);
        if (scaledRounded <= 0m)
        {
            var alerts = BuildBlockAlerts(p, ctx, breachLeverage, breachMargin, breachPerPos, breachBasket, observations);
            return new EnforcementResult(false, scaledBefore, scaledRounded, observations, alerts);
        }
        var scaleRatio = scaledRounded / p.RequestedVolume;
        // Recompute post-round metrics assuming linear scaling for notional, margin, risk measures.
        var postLeverage = projectedLeverage * scaleRatio;
        var postMargin = projectedMarginUsagePct * scaleRatio;
        var postPerPos = perPositionRiskPct * scaleRatio;
        var postBasket = basketRiskPct * scaleRatio; // linear assumption
        bool postOk = postLeverage <= ctx.Config.RealLeverageCap && postMargin <= ctx.Config.MarginUsageCapPct && postPerPos <= ctx.Config.PerPositionRiskCapPct && postBasket <= ctx.Config.PerPositionRiskCapPct;
        var obs2 = new RiskObservations(projectedLeverage, projectedMarginUsagePct, perPositionRiskPct, basketRiskPct, postLeverage, postMargin);
        if (!postOk)
        {
            var alerts = BuildBlockAlerts(p, ctx, breachLeverage, breachMargin, breachPerPos, breachBasket, obs2);
            return new EnforcementResult(false, scaledBefore, scaledRounded, obs2, alerts);
        }
        // Allowed with scale info
        var infoAlert = new AlertEvent(
            AlertTypes.INFO_SCALE,
            p.DecisionId,
            p.InstrumentId,
            $"Scaled from {p.RequestedVolume} to {scaledRounded} (pre-round {scaledBefore})",
            projectedLeverage, // Observed = we report original leverage as base context
            ctx.Config.RealLeverageCap,
            ctx.Equity,
            p.NotionalValue,
            p.UsedMarginAfterOrder,
            _schemaVersion,
            _configHash
        );
        return new EnforcementResult(true, scaledBefore, scaledRounded, obs2, new[] { infoAlert });
    }

    private IReadOnlyList<AlertEvent> BuildBlockAlerts(Proposal p, RiskContext ctx, bool breachLeverage, bool breachMargin, bool breachPerPos, bool breachBasket, RiskObservations obs)
    {
        var list = new List<AlertEvent>();
        if (breachLeverage) list.Add(new AlertEvent(AlertTypes.BLOCK_LEVERAGE, p.DecisionId, p.InstrumentId, "Leverage cap breach", obs.ProjectedLeverage, ctx.Config.RealLeverageCap, ctx.Equity, p.NotionalValue, p.UsedMarginAfterOrder, Schema.Version, _configHash));
        if (breachMargin) list.Add(new AlertEvent(AlertTypes.BLOCK_MARGIN, p.DecisionId, p.InstrumentId, "Margin usage cap breach", obs.ProjectedMarginUsagePct, ctx.Config.MarginUsageCapPct, ctx.Equity, p.NotionalValue, p.UsedMarginAfterOrder, Schema.Version, _configHash));
        if (breachPerPos) list.Add(new AlertEvent(AlertTypes.BLOCK_RISK_CAP, p.DecisionId, p.InstrumentId, "Per-position risk cap breach", obs.PerPositionRiskPct, ctx.Config.PerPositionRiskCapPct, ctx.Equity, p.NotionalValue, p.UsedMarginAfterOrder, Schema.Version, _configHash));
        if (breachBasket) list.Add(new AlertEvent(AlertTypes.BLOCK_BASKET, p.DecisionId, p.InstrumentId, "Basket risk cap breach", obs.BasketRiskPct, ctx.Config.PerPositionRiskCapPct, ctx.Equity, p.NotionalValue, p.UsedMarginAfterOrder, Schema.Version, _configHash));
        return new ReadOnlyCollection<AlertEvent>(list);
    }

    private static BasketMode ParseBasketMode(string mode) => Enum.TryParse<BasketMode>(mode, true, out var m) ? m : BasketMode.Base;
    private static decimal SafeDiv(decimal a, decimal b) => b == 0m ? 0m : decimal.Round(a / b, 6, MidpointRounding.AwayFromZero);
    private static decimal Min(params decimal[] values) { decimal m = values[0]; for (int i = 1; i < values.Length; i++) if (values[i] < m) m = values[i]; return m; }
    private static decimal RoundDownToStep(decimal value, decimal step) => step <= 0 ? value : Math.Floor(value / step) * step;
}

internal static class RiskContextExtensions
{
    // Temporary config hash accessor until integrated deeper
    public static string ConfigHashPlaceholder(this RiskConfig cfg) => ""; // Will be populated by Sim when journaling
    public static string ConfigHashPlaceholder(this RiskContext ctx) => ctx.Config.ConfigHashPlaceholder();
}
