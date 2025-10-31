using System.Globalization;
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
        var buckets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (TryObject(riskEl, "instrument_buckets", out var bEl))
        {
            foreach (var p in bEl.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) buckets[p.Name] = p.Value.GetString() ?? string.Empty;
        }
        var sessionWindow = ParseSessionWindow(riskEl);
        var dailyCap = ParseDailyCap(riskEl);
        var globalDrawdown = ParseGlobalDrawdown(riskEl);
        decimal? legacyDrawdown = TryNumber(riskEl, "max_run_drawdown_ccy", out var dd) ? dd : null;
        var maxRunDrawdown = globalDrawdown?.MaxDrawdown ?? legacyDrawdown;
        var newsBlackout = ParseNewsBlackout(riskEl);
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
            MaxRunDrawdownCCY = maxRunDrawdown,
            BlockOnBreach = Bool("block_on_breach", true),
            EmitEvaluations = Bool("emit_evaluations", true),
            MaxNetExposureBySymbol = ParseExposureCaps(riskEl),
            SessionWindow = sessionWindow,
            DailyCap = dailyCap,
            GlobalDrawdown = globalDrawdown ?? (legacyDrawdown.HasValue ? new GlobalDrawdownConfig(legacyDrawdown.Value) : null),
            NewsBlackout = newsBlackout,
            RiskConfigHash = TryCanonicalHash(riskEl)
        };
    }

    private static bool TryNumber(JsonElement parent, string snake, out decimal value)
    {
        if (TryProperty(parent, snake, out var el) && el.ValueKind == JsonValueKind.Number) { value = el.GetDecimal(); return true; }
        if (TryProperty(parent, SnakeToCamel(snake), out var camel) && camel.ValueKind == JsonValueKind.Number) { value = camel.GetDecimal(); return true; }
        value = 0; return false;
    }
    private static bool TryBool(JsonElement parent, string snake, out bool value)
    {
        if (TryProperty(parent, snake, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)) { value = el.GetBoolean(); return true; }
        if (TryProperty(parent, SnakeToCamel(snake), out var camel) && (camel.ValueKind == JsonValueKind.True || camel.ValueKind == JsonValueKind.False)) { value = camel.GetBoolean(); return true; }
        value = false; return false;
    }
    private static bool TryString(JsonElement parent, string snake, out string? value)
    {
        if (TryProperty(parent, snake, out var el) && el.ValueKind == JsonValueKind.String) { value = el.GetString(); return true; }
        if (TryProperty(parent, SnakeToCamel(snake), out var camel) && camel.ValueKind == JsonValueKind.String) { value = camel.GetString(); return true; }
        value = null; return false;
    }
    private static bool TryObject(JsonElement parent, string snake, out JsonElement obj)
    {
        if (TryProperty(parent, snake, out var el) && el.ValueKind == JsonValueKind.Object) { obj = el; return true; }
        if (TryProperty(parent, SnakeToCamel(snake), out var camel) && camel.ValueKind == JsonValueKind.Object) { obj = camel; return true; }
        obj = default; return false;
    }
    private static bool TryInt(JsonElement parent, string snake, out int value)
    {
        if (TryProperty(parent, snake, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)) { value = v; return true; }
        if (TryProperty(parent, SnakeToCamel(snake), out var camel) && camel.ValueKind == JsonValueKind.Number && camel.TryGetInt32(out var v2)) { value = v2; return true; }
        value = 0; return false;
    }
    private static bool TryProperty(JsonElement parent, string name, out JsonElement el)
    {
        if (parent.TryGetProperty(name, out el)) return true; el = default; return false;
    }
    private static string SnakeToCamel(string snake)
    {
        return string.Concat(snake.Split('_', StringSplitOptions.RemoveEmptyEntries).Select((s, i) => i == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1)));
    }

    private static Dictionary<string, decimal>? ParseExposureCaps(JsonElement parent)
    {
        if (TryObject(parent, "max_net_exposure_by_symbol", out var obj))
        {
            var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in obj.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) dict[p.Name] = p.Value.GetDecimal();
            return dict.Count > 0 ? dict : null;
        }
        if (TryObject(parent, "maxNetExposureBySymbol", out var camel))
        {
            var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in camel.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) dict[p.Name] = p.Value.GetDecimal();
            return dict.Count > 0 ? dict : null;
        }
        return null;
    }

    private static SessionWindowConfig? ParseSessionWindow(JsonElement parent)
    {
        if (!TryObject(parent, "session_window", out var obj)) return null;
        if (!TryString(obj, "start_utc", out var startRaw) || string.IsNullOrWhiteSpace(startRaw))
            throw new FormatException("session_window.start_utc must be provided.");
        if (!TryString(obj, "end_utc", out var endRaw) || string.IsNullOrWhiteSpace(endRaw))
            throw new FormatException("session_window.end_utc must be provided.");
        var start = ParseTimeOfDay(startRaw);
        var end = ParseTimeOfDay(endRaw);
        return new SessionWindowConfig(start, end);
    }

    private static DailyCapConfig? ParseDailyCap(JsonElement parent)
    {
        if (!TryObject(parent, "daily_cap", out var obj)) return null;
        decimal? loss = TryNumber(obj, "loss", out var lossVal) ? lossVal : null;
        decimal? gain = TryNumber(obj, "gain", out var gainVal) ? gainVal : null;
        if (loss is null && gain is null) return null;
        var actionString = TryString(obj, "action_on_breach", out var actRaw) && !string.IsNullOrWhiteSpace(actRaw)
            ? actRaw
            : "block";
        var action = ParseDailyCapAction(actionString);
        return new DailyCapConfig(loss, gain, action);
    }

    private static GlobalDrawdownConfig? ParseGlobalDrawdown(JsonElement parent)
    {
        if (TryObject(parent, "global_drawdown", out var obj) && TryNumber(obj, "max_dd", out var dd))
        {
            return new GlobalDrawdownConfig(dd);
        }
        return null;
    }

    private static NewsBlackoutConfig? ParseNewsBlackout(JsonElement parent)
    {
        if (!TryObject(parent, "news_blackout", out var obj)) return null;
        var enabled = TryBool(obj, "enabled", out var e) ? e : false;
        int minutesBefore = TryInt(obj, "minutes_before", out var before) ? before : 0;
        int minutesAfter = TryInt(obj, "minutes_after", out var after) ? after : 0;
        string? sourcePath = (TryString(obj, "source_path", out var source) && !string.IsNullOrWhiteSpace(source)) ? source : null;
        return new NewsBlackoutConfig(enabled, minutesBefore, minutesAfter, sourcePath);
    }

    private static TimeSpan ParseTimeOfDay(string raw)
    {
        var formats = new[] { @"hh\:mm", @"hh\:mm\:ss" };
        if (TimeSpan.TryParseExact(raw, formats, CultureInfo.InvariantCulture, out var ts))
        {
            return ts;
        }
        throw new FormatException($"Invalid time of day '{raw}'. Expected HH:mm or HH:mm:ss.");
    }

    private static DailyCapAction ParseDailyCapAction(string raw)
    {
        var normalized = raw.Trim().ToLowerInvariant();
        return normalized switch
        {
            "block" => DailyCapAction.Block,
            "half_size" => DailyCapAction.HalfSize,
            "half-size" => DailyCapAction.HalfSize,
            "halfsize" => DailyCapAction.HalfSize,
            _ => throw new FormatException($"Unsupported daily cap action '{raw}'. Expected 'block' or 'half_size'.")
        };
    }

    private static string? TryCanonicalHash(JsonElement riskEl)
    {
        try
        {
            var canonical = JsonCanonicalizer.Canonicalize(riskEl);
            return ConfigHash.Compute(canonical);
        }
        catch
        {
            return null;
        }
    }
}
