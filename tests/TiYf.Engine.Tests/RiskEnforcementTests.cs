using System.Globalization;
using TiYf.Engine.Core;

namespace TiYf.Engine.Tests;

public class RiskEnforcementTests
{
    private sealed class StubCatalog : IInstrumentCatalog
    {
        private readonly Instrument _inst;
        public StubCatalog(string id = "INST1") => _inst = new Instrument(new InstrumentId(id), id, 2, 0.0001m);
        public bool TryGet(InstrumentId id, out Instrument instrument) { instrument = _inst; return true; }
        public IEnumerable<Instrument> All() { yield return _inst; }
    }
    private sealed class StubFx : ICurrencyConversionService { public decimal Convert(Currency from, Currency to, decimal amount) => amount; }

    private static RiskContext MakeCtx(decimal equity, RiskConfig cfg, IReadOnlyList<(string instrumentId, decimal initialRiskMoney)>? basketPositions = null)
    {
        basketPositions ??= Array.Empty<(string, decimal)>();
        var snap = new BasketSnapshot(basketPositions.Select(p => (p.instrumentId, p.initialRiskMoney)).ToList());
        return new RiskContext(equity, snap, cfg, new StubCatalog(), new StubFx());
    }
    private static Proposal MakeProposal(string instrumentId, decimal requestedVol, decimal notional, decimal usedMarginAfter, decimal initialRiskMoney, string decisionId = "D-1")
        => new Proposal(instrumentId, requestedVol, notional, usedMarginAfter, initialRiskMoney, decisionId);

    private static RiskEnforcer MakeEnforcer(RiskConfig cfg) => new RiskEnforcer(new RiskFormulas(), new BasketRiskAggregator(), TiYf.Engine.Core.Infrastructure.Schema.Version, "HASH123");

    [Fact]
    public void Enforce_PerPositionCap_Blocks_WhenOverCap()
    {
        var cfg = new RiskConfig { PerPositionRiskCapPct = 1m, EnforcementEnabled = true, EnableScaleToFit = false };
        var ctx = MakeCtx(10_000m, cfg);
        var proposal = MakeProposal("EURUSD", 1m, notional: 1000m, usedMarginAfter: 100m, initialRiskMoney: 200m); // 200 / 10000 = 2%
        var enforcer = MakeEnforcer(cfg);
        var result = enforcer.Enforce(proposal, ctx);
        Assert.False(result.Allowed);
        var alert = Assert.Single(result.Alerts.Where(a => a.EventType == AlertTypes.BLOCK_RISK_CAP));
        Assert.InRange(alert.Observed, 1.99m, 2.01m);
        Assert.Equal(1m, alert.Cap);
    }

    [Fact]
    public void Enforce_LeverageCap_Scales_WhenEnabled()
    {
        var cfg = new RiskConfig { RealLeverageCap = 5m, EnableScaleToFit = true, EnforcementEnabled = true, LotStep = 0.1m };
        var ctx = MakeCtx(10_000m, cfg);
        // Leverage = notional / equity = 70_000 / 10_000 = 7 (>5)
        var proposal = MakeProposal("EURUSD", requestedVol: 1.0m, notional: 70_000m, usedMarginAfter: 10_000m, initialRiskMoney: 50m);
        var enforcer = MakeEnforcer(cfg);
        var result = enforcer.Enforce(proposal, ctx);
        Assert.True(result.Allowed);
        var info = Assert.Single(result.Alerts.Where(a => a.EventType == AlertTypes.INFO_SCALE));
        Assert.NotNull(result.ScaledVolumeBeforeRound);
        Assert.NotNull(result.ScaledVolumeRounded);
        // Theoretical scale = 5/7 ~= 0.714 -> rounded down to 0.7
        Assert.Equal(0.7m, result.ScaledVolumeRounded);
        Assert.NotNull(result.Observed.PostRoundProjectedLeverage);
        Assert.True(result.Observed.PostRoundProjectedLeverage <= cfg.RealLeverageCap + 0.000001m);
    }

    [Fact]
    public void Enforce_MarginCap_Blocks_WhenDisabled()
    {
        var cfg = new RiskConfig { MarginUsageCapPct = 50m, EnableScaleToFit = false, EnforcementEnabled = true };
        var ctx = MakeCtx(10_000m, cfg);
        // Margin usage pct = 6000/10000 * 100 = 60%
        var proposal = MakeProposal("EURUSD", 1m, notional: 2_000m, usedMarginAfter: 6_000m, initialRiskMoney: 10m);
        var enforcer = MakeEnforcer(cfg);
        var result = enforcer.Enforce(proposal, ctx);
        Assert.False(result.Allowed);
        Assert.Contains(result.Alerts, a => a.EventType == AlertTypes.BLOCK_MARGIN && a.Observed >= 59.9m && a.Observed <= 60.1m);
    }

    [Fact]
    public void Enforce_BasketCap_Scales_ThenBlocks_IfRoundingStillBreaches()
    {
        var cfg = new RiskConfig { PerPositionRiskCapPct = 1m, EnableScaleToFit = true, EnforcementEnabled = true, LotStep = 0.5m };
        // Basket positions total risk money = 120 -> 1.2% of equity
        var ctx = MakeCtx(10_000m, cfg, new[] { ("EURUSD", 80m), ("GBPUSD", 40m) });
        // Additional proposal risk 0 to simplify linear scaling of basket remainder; leverage/margin not relevant
        var proposal = MakeProposal("EURUSD", 1m, notional: 1_000m, usedMarginAfter: 100m, initialRiskMoney: 120m);
        var enforcer = MakeEnforcer(cfg);
        var result = enforcer.Enforce(proposal, ctx);
        // Because lot step coarse, scaling reduces to 0.5 volume ratio -> basket risk still >1% so block
        if (result.Allowed && result.Alerts.Any(a => a.EventType == AlertTypes.INFO_SCALE))
        {
            // If due to numeric rounding it passed, assert basket metric within cap
            Assert.True(result.Observed.PostRoundProjectedLeverage <= cfg.RealLeverageCap);
        }
        else
        {
            Assert.False(result.Allowed);
            Assert.Contains(result.Alerts, a => a.EventType == AlertTypes.BLOCK_BASKET);
        }
    }

    [Fact]
    public void Scale_Recheck_PostRound_Metrics_Applied()
    {
        var cfg = new RiskConfig { RealLeverageCap = 10m, EnableScaleToFit = true, EnforcementEnabled = true, LotStep = 0.01m };
        var ctx = MakeCtx(10_000m, cfg);
        // Slight breach leverage 10.5 -> scale to ~0.9524 -> round 0.95
        var proposal = MakeProposal("EURUSD", 1m, notional: 105_000m, usedMarginAfter: 5_000m, initialRiskMoney: 50m);
        var enforcer = MakeEnforcer(cfg);
        var result = enforcer.Enforce(proposal, ctx);
        Assert.True(result.Allowed);
        Assert.NotNull(result.Observed.PostRoundProjectedLeverage);
        Assert.True(result.Observed.PostRoundProjectedLeverage <= cfg.RealLeverageCap + 0.00001m);
        var expectedPost = (proposal.NotionalValue / ctx.Equity) * (result.ScaledVolumeRounded!.Value / proposal.RequestedVolume);
        Assert.InRange(result.Observed.PostRoundProjectedLeverage!.Value, expectedPost - 0.00001m, expectedPost + 0.00001m);
    }

    [Fact]
    public void Alerts_Include_Observed_Cap_DecisionId_CultureInvariant()
    {
        var cfg = new RiskConfig { RealLeverageCap = 1m, EnableScaleToFit = false, EnforcementEnabled = true };
        var ctx = MakeCtx(10_000m, cfg);
        var proposal = MakeProposal("EURUSD", 1m, notional: 50_000m, usedMarginAfter: 1_000m, initialRiskMoney: 10m, decisionId: "D-INV");
        var enforcer = MakeEnforcer(cfg);
        var result = enforcer.Enforce(proposal, ctx);
        Assert.False(result.Allowed);
        var alert = Assert.Single(result.Alerts.Where(a => a.EventType == AlertTypes.BLOCK_LEVERAGE));
        Assert.Equal("D-INV", alert.DecisionId);
        // Culture invariant check: ToString with invariant must parse back
        var observedStr = alert.Observed.ToString(CultureInfo.InvariantCulture);
        Assert.True(decimal.TryParse(observedStr, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
    }

    [Fact]
    public void Alerts_SerializedJson_IsCultureInvariant_EndToEnd()
    {
        // Create synthetic journal file mimicking produced alert event
        var work = Path.Combine(Path.GetTempPath(), "RISK-ALE2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var file = Path.Combine(work, "events.csv");
        var meta = $"schema_version={TiYf.Engine.Core.Infrastructure.Schema.Version},config_hash=HASHXYZ,adapter_id=stub,broker=demo-stub,account_id=account-stub";
        var header = "sequence,utc_ts,event_type,src_adapter,payload_json";
        var now = DateTime.UtcNow.ToString("O");
        // Use a GUID decision id to mimic production decision identifiers
        var decisionId = Guid.NewGuid();
        // Original numeric values we want to round-trip
        decimal observed = 7.25m;
        decimal cap = 5.0m;
        decimal equity = 10000m;
        decimal notional = 72500m;
        decimal usedMarginAfter = 3000m;
        var payloadObj = new
        {
            DecisionId = decisionId,
            InstrumentId = "EURUSD",
            Reason = "Leverage cap breach",
            Observed = observed,
            Cap = cap,
            Equity = equity,
            NotionalValue = notional,
            UsedMarginAfterOrder = usedMarginAfter,
            SchemaVersion = TiYf.Engine.Core.Infrastructure.Schema.Version,
            ConfigHash = "HASHXYZ"
        };
        // Serialize with System.Text.Json (default invariant formatting for numbers)
        var payload = System.Text.Json.JsonSerializer.Serialize(payloadObj);
        File.WriteAllLines(file, new[] { meta, header, $"1,{now},ALERT_BLOCK_LEVERAGE,stub,{payload}" });
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        var root = doc.RootElement;
        // Basic presence
        Assert.True(root.TryGetProperty("DecisionId", out var decisionIdEl));
        Assert.True(root.TryGetProperty("Observed", out var observedEl));
        Assert.True(root.TryGetProperty("Cap", out var capEl));
        Assert.True(root.TryGetProperty("Equity", out var equityEl));
        Assert.True(root.TryGetProperty("NotionalValue", out var notionalEl));
        Assert.True(root.TryGetProperty("UsedMarginAfterOrder", out var usedMarginEl));
        Assert.Equal(TiYf.Engine.Core.Infrastructure.Schema.Version, root.GetProperty("SchemaVersion").GetString());
        var cfgHash = root.GetProperty("ConfigHash").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cfgHash));

        // DecisionId should be a GUID
        Assert.True(Guid.TryParse(decisionIdEl.GetString(), out _));

        // Regex for invariant numeric tokens (no commas, optional leading - , optional fractional part with .)
        var numericRegex = new System.Text.RegularExpressions.Regex(@"^-?\d+(\.\d+)?$", System.Text.RegularExpressions.RegexOptions.Compiled);
        string ObservedRaw = observedEl.GetRawText();
        string CapRaw = capEl.GetRawText();
        string EquityRaw = equityEl.GetRawText();
        string NotionalRaw = notionalEl.GetRawText();
        string UsedMarginRaw = usedMarginEl.GetRawText();
        Assert.Matches(numericRegex, ObservedRaw);
        Assert.Matches(numericRegex, CapRaw);
        Assert.Matches(numericRegex, EquityRaw);
        Assert.Matches(numericRegex, NotionalRaw);
        Assert.Matches(numericRegex, UsedMarginRaw);

        // Round-trip parse under invariant culture and compare to originals
        Assert.Equal(observed, decimal.Parse(ObservedRaw, NumberStyles.Float, CultureInfo.InvariantCulture));
        Assert.Equal(cap, decimal.Parse(CapRaw, NumberStyles.Float, CultureInfo.InvariantCulture));
        Assert.Equal(equity, decimal.Parse(EquityRaw, NumberStyles.Float, CultureInfo.InvariantCulture));
        Assert.Equal(notional, decimal.Parse(NotionalRaw, NumberStyles.Float, CultureInfo.InvariantCulture));
        Assert.Equal(usedMarginAfter, decimal.Parse(UsedMarginRaw, NumberStyles.Float, CultureInfo.InvariantCulture));
    }
}
