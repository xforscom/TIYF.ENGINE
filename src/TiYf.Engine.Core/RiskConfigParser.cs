using System.Text.Json;

namespace TiYf.Engine.Core;

public static class RiskConfigParser
{
    public static RiskConfig Parse(JsonElement riskEl)
    {
        decimal Num(string snake, decimal fallback)
        {
            if (TryNumber(riskEl, snake, out var v)) return v; return fallback;
        }
        bool Bool(string snake, bool fallback)
        {
            if (TryBool(riskEl, snake, out var v)) return v; return fallback;
        }
        string Str(string snake, string fallback)
        {
            if (TryString(riskEl, snake, out var v) && v is not null) return v;
            return fallback;
        }
        var buckets = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if (TryObject(riskEl, "instrument_buckets", out var bEl))
        {
            foreach (var p in bEl.EnumerateObject()) if (p.Value.ValueKind==JsonValueKind.String) buckets[p.Name]=p.Value.GetString()??string.Empty;
        }
        return new RiskConfig
        {
            RealLeverageCap = Num("real_leverage_cap", 20m),
            MarginUsageCapPct = Num("margin_usage_cap_pct", 80m),
            PerPositionRiskCapPct = Num("per_position_risk_cap_pct", 1m),
            BasketMode = Str("basket_mode", "Base"),
            InstrumentBuckets = buckets,
            EnableScaleToFit = Bool("enable_scale_to_fit", false),
            EnforcementEnabled = Bool("enforcement_enabled", true),
            LotStep = Num("lot_step", 0.01m),
            MaxRunDrawdownCCY = TryNumber(riskEl, "max_run_drawdown_ccy", out var dd) ? dd : null,
            BlockOnBreach = Bool("block_on_breach", true),
            EmitEvaluations = Bool("emit_evaluations", true),
            MaxNetExposureBySymbol = ParseExposureCaps(riskEl)
        };
    }

    private static bool TryNumber(JsonElement parent, string snake, out decimal value)
    {
        if (TryProperty(parent, snake, out var el) && el.ValueKind==JsonValueKind.Number) { value = el.GetDecimal(); return true; }
        if (TryProperty(parent, SnakeToCamel(snake), out var camel) && camel.ValueKind==JsonValueKind.Number) { value = camel.GetDecimal(); return true; }
        value = 0; return false;
    }
    private static bool TryBool(JsonElement parent, string snake, out bool value)
    {
        if (TryProperty(parent, snake, out var el) && (el.ValueKind==JsonValueKind.True||el.ValueKind==JsonValueKind.False)) { value = el.GetBoolean(); return true; }
        if (TryProperty(parent, SnakeToCamel(snake), out var camel) && (camel.ValueKind==JsonValueKind.True||camel.ValueKind==JsonValueKind.False)) { value = camel.GetBoolean(); return true; }
        value=false; return false;
    }
    private static bool TryString(JsonElement parent, string snake, out string? value)
    {
        if (TryProperty(parent, snake, out var el) && el.ValueKind==JsonValueKind.String){ value = el.GetString(); return true; }
        if (TryProperty(parent, SnakeToCamel(snake), out var camel) && camel.ValueKind==JsonValueKind.String){ value = camel.GetString(); return true; }
        value=null; return false;
    }
    private static bool TryObject(JsonElement parent, string snake, out JsonElement obj)
    {
        if (TryProperty(parent, snake, out var el) && el.ValueKind==JsonValueKind.Object){ obj=el; return true; }
        if (TryProperty(parent, SnakeToCamel(snake), out var camel) && camel.ValueKind==JsonValueKind.Object){ obj=camel; return true; }
        obj = default; return false;
    }
    private static bool TryProperty(JsonElement parent, string name, out JsonElement el)
    {
        if (parent.TryGetProperty(name, out el)) return true; el=default; return false;
    }
    private static string SnakeToCamel(string snake)
    {
        return string.Concat(snake.Split('_', StringSplitOptions.RemoveEmptyEntries).Select((s,i)=> i==0? s: char.ToUpperInvariant(s[0])+s.Substring(1)));
    }

    private static Dictionary<string, decimal>? ParseExposureCaps(JsonElement parent)
    {
        if (TryObject(parent, "max_net_exposure_by_symbol", out var obj))
        {
            var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in obj.EnumerateObject()) if (p.Value.ValueKind==JsonValueKind.Number) dict[p.Name] = p.Value.GetDecimal();
            return dict.Count>0? dict : null;
        }
        if (TryObject(parent, "maxNetExposureBySymbol", out var camel))
        {
            var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in camel.EnumerateObject()) if (p.Value.ValueKind==JsonValueKind.Number) dict[p.Name] = p.Value.GetDecimal();
            return dict.Count>0? dict : null;
        }
        return null;
    }
}
