using System;
using System.Collections.Generic;
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
        var globalVolatilityGate = ParseGlobalVolatilityGate(riskEl);
        var promotion = ParsePromotionConfig(riskEl);
        decimal? brokerDailyLossCap = TryNumber(riskEl, "broker_daily_loss_cap_ccy", out var brokerCap) && brokerCap > 0 ? brokerCap : null;
        long? maxPositionUnits = TryLong(riskEl, "max_position_units", out var maxUnits) && maxUnits > 0 ? maxUnits : null;
        var symbolUnitCaps = ParseSymbolUnitCaps(riskEl);
        var cooldown = ParseCooldown(riskEl);
        var riskRailsMode = ParseRiskRailsMode(riskEl);
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
            MaxUnitsPerSymbol = ParseUnitsCaps(riskEl),
            SessionWindow = sessionWindow,
            DailyCap = dailyCap,
            GlobalDrawdown = globalDrawdown ?? (legacyDrawdown.HasValue ? new GlobalDrawdownConfig(legacyDrawdown.Value) : null),
            NewsBlackout = newsBlackout,
            Promotion = promotion,
            GlobalVolatilityGate = globalVolatilityGate,
            BrokerDailyLossCapCcy = brokerDailyLossCap,
            MaxPositionUnits = maxPositionUnits,
            SymbolUnitCaps = symbolUnitCaps,
            Cooldown = cooldown,
            RiskRailsMode = riskRailsMode,
            RiskConfigHash = TryCanonicalHash(riskEl)
        };
    }

    private static PromotionConfig ParsePromotionConfig(JsonElement riskEl)
    {
        const bool defaultEnabled = false;
        const int defaultProbationDays = 30;
        const int defaultMinTrades = 50;
        const decimal defaultPromotionThreshold = 0.6m;
        const decimal defaultDemotionThreshold = 0.4m;

        bool enabled = defaultEnabled;
        IReadOnlyList<string> shadowCandidates = Array.Empty<string>();
        int probationDays = defaultProbationDays;
        int minTrades = defaultMinTrades;
        decimal promotionThreshold = defaultPromotionThreshold;
        decimal demotionThreshold = defaultDemotionThreshold;
        string hash;

        if (TryObject(riskEl, "promotion", out var promotionEl))
        {
            enabled = TryBool(promotionEl, "enabled", out var enabledValue) ? enabledValue : defaultEnabled;

            if (TryArray(promotionEl, "shadow_candidates", out var candidatesEl))
            {
                var list = new List<string>();
                foreach (var candidate in candidatesEl.EnumerateArray())
                {
                    if (candidate.ValueKind != JsonValueKind.String) continue;
                    var raw = candidate.GetString();
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var trimmed = raw.Trim();
                    if (trimmed.Length > 0) list.Add(trimmed);
                }
                shadowCandidates = list.Count > 0 ? list.ToArray() : Array.Empty<string>();
            }

            probationDays = TryInt(promotionEl, "probation_days", out var probation) ? Math.Max(0, probation) : defaultProbationDays;
            minTrades = TryInt(promotionEl, "min_trades", out var trades) ? Math.Max(0, trades) : defaultMinTrades;
            promotionThreshold = TryNumber(promotionEl, "promotion_threshold", out var promote) ? ClampProbability(promote) : defaultPromotionThreshold;
            demotionThreshold = TryNumber(promotionEl, "demotion_threshold", out var demote) ? ClampProbability(demote) : defaultDemotionThreshold;
            hash = TryCanonicalHash(promotionEl) ?? ComputeDefaultPromotionHash();
        }
        else
        {
            hash = ComputeDefaultPromotionHash();
        }

        return new PromotionConfig(
            enabled,
            shadowCandidates,
            probationDays,
            minTrades,
            promotionThreshold,
            demotionThreshold,
            hash);
    }

    private static string ParseRiskRailsMode(JsonElement riskEl)
    {
        var mode = "telemetry";
        if (TryString(riskEl, "risk_rails_mode", out var value) && !string.IsNullOrWhiteSpace(value))
        {
            mode = value.Trim();
        }
        else if (TryString(riskEl, "riskRailsMode", out var camel) && !string.IsNullOrWhiteSpace(camel))
        {
            mode = camel.Trim();
        }

        return mode.ToLowerInvariant();
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
    private static bool TryArray(JsonElement parent, string snake, out JsonElement array)
    {
        if (TryProperty(parent, snake, out var el) && el.ValueKind == JsonValueKind.Array) { array = el; return true; }
        if (TryProperty(parent, SnakeToCamel(snake), out var camel) && camel.ValueKind == JsonValueKind.Array) { array = camel; return true; }
        array = default; return false;
    }
    private static bool TryInt(JsonElement parent, string snake, out int value)
    {
        if (TryProperty(parent, snake, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)) { value = v; return true; }
        if (TryProperty(parent, SnakeToCamel(snake), out var camel) && camel.ValueKind == JsonValueKind.Number && camel.TryGetInt32(out var v2)) { value = v2; return true; }
        value = 0; return false;
    }
    private static bool TryLong(JsonElement parent, string snake, out long value)
    {
        if (TryProperty(parent, snake, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v)) { value = v; return true; }
        if (TryProperty(parent, SnakeToCamel(snake), out var camel) && camel.ValueKind == JsonValueKind.Number && camel.TryGetInt64(out var v2)) { value = v2; return true; }
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

    private static IReadOnlyDictionary<string, string>? ParseStringDictionary(JsonElement parent, string propertyName)
    {
        if (!TryObject(parent, propertyName, out var obj)) return null;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                var value = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    dict[prop.Name] = value;
                }
            }
        }
        return dict.Count == 0 ? null : dict;
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

    private static Dictionary<string, long>? ParseUnitsCaps(JsonElement parent)
    {
        if (TryObject(parent, "max_units_per_symbol", out var obj))
        {
            var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in obj.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var units))
                {
                    dict[p.Name] = units;
                }
            }
            return dict.Count > 0 ? dict : null;
        }
        if (TryObject(parent, "maxUnitsPerSymbol", out var camel))
        {
            var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in camel.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var units))
                {
                    dict[p.Name] = units;
                }
            }
            return dict.Count > 0 ? dict : null;
        }
        return null;
    }
    private static Dictionary<string, long>? ParseSymbolUnitCaps(JsonElement parent)
    {
        if (TryObject(parent, "symbol_unit_caps", out var snake))
        {
            return ParsePositiveUnitsDictionary(snake);
        }
        if (TryObject(parent, "symbolUnitCaps", out var camel))
        {
            return ParsePositiveUnitsDictionary(camel);
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
        int pollSeconds = TryInt(obj, "poll_seconds", out var poll) ? Math.Max(5, poll) : 60;
        var sourceTypeRaw = TryString(obj, "source_type", out var st) ? st : null;
        var sourceType = NewsSourceTypeHelper.Normalize(sourceTypeRaw);
        NewsHttpSourceConfig? httpConfig = null;
        if (TryObject(obj, "http", out var httpNode))
        {
            var baseUri = TryString(httpNode, "base_uri", out var baseRaw) ? baseRaw : null;
            var headerName = TryString(httpNode, "api_key_header", out var headerRaw) ? headerRaw : null;
            var apiKeyEnv = TryString(httpNode, "api_key_env", out var envRaw) ? envRaw : null;
            var headers = ParseStringDictionary(httpNode, "headers");
            var query = ParseStringDictionary(httpNode, "query");
            if (!string.IsNullOrWhiteSpace(baseUri))
            {
                httpConfig = new NewsHttpSourceConfig(baseUri, headerName, apiKeyEnv, headers, query);
            }
        }
        return new NewsBlackoutConfig(enabled, minutesBefore, minutesAfter, sourcePath, pollSeconds, sourceType, httpConfig);
    }

    private static GlobalVolatilityGateConfig ParseGlobalVolatilityGate(JsonElement parent)
    {
        if (!TryObject(parent, "global_volatility_gate", out var obj))
        {
            return GlobalVolatilityGateConfig.Disabled;
        }

        var mode = TryString(obj, "enabled_mode", out var modeRaw) && !string.IsNullOrWhiteSpace(modeRaw)
            ? modeRaw.Trim()
            : "disabled";
        var entryThreshold = TryNumber(obj, "entry_threshold", out var threshold) ? threshold : 0m;
        var ewmaAlpha = TryNumber(obj, "ewma_alpha", out var alpha) ? ClampAlpha(alpha) : 0.3m;

        var components = new List<GlobalVolatilityComponentConfig>();
        if (TryArray(obj, "components", out var arr))
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!TryString(item, "name", out var nameRaw) || string.IsNullOrWhiteSpace(nameRaw)) continue;
                var weight = TryNumber(item, "weight", out var weightVal) ? weightVal : 0m;
                components.Add(new GlobalVolatilityComponentConfig(nameRaw.Trim(), weight));
            }
        }

        if (components.Count == 0)
        {
            components.AddRange(GlobalVolatilityGateConfig.Disabled.EffectiveComponents);
        }

        var liveMaxBucket = NormalizeLiveBucket(obj.TryGetProperty("live_max_bucket", out var bucketEl) && bucketEl.ValueKind == JsonValueKind.String
            ? bucketEl.GetString()
            : null);
        decimal? liveMaxEwma = null;
        if (TryNumber(obj, "live_max_ewma", out var liveEwma))
        {
            liveMaxEwma = liveEwma;
        }

        return new GlobalVolatilityGateConfig(
            mode,
            entryThreshold,
            ewmaAlpha,
            components,
            liveMaxBucket,
            liveMaxEwma);
    }

    private static RiskCooldownConfig ParseCooldown(JsonElement parent)
    {
        if (!TryObject(parent, "cooldown", out var obj))
        {
            return RiskCooldownConfig.Disabled;
        }

        var enabled = TryBool(obj, "enabled", out var e) && e;
        int? consecutive = TryInt(obj, "consecutive_losses", out var losses) ? Math.Max(0, losses) : null;
        int? minutes = TryInt(obj, "minutes", out var mins) ? Math.Max(0, mins) : null;

        if (!enabled)
        {
            return new RiskCooldownConfig(false, consecutive, minutes);
        }

        if (!consecutive.HasValue || consecutive.Value <= 0 || !minutes.HasValue || minutes.Value <= 0)
        {
            return RiskCooldownConfig.Disabled;
        }

        return new RiskCooldownConfig(true, consecutive.Value, minutes.Value);
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

    private static string? NormalizeLiveBucket(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        return normalized switch
        {
            "calm" => "calm",
            "moderate" => "moderate",
            "volatile" => "volatile",
            _ => null
        };
    }

    // --- parser-local helpers (promotion) ---
    private static decimal ClampProbability(decimal p)
    {
        if (p < 0m) return 0m;
        if (p > 1m) return 1m;
        return p;
    }

    private static string ComputeDefaultPromotionHash()
    {
        // Keep parity with defaults: empty hash when promotion block is absent.
        return string.Empty;
    }

    private static decimal ClampAlpha(decimal alpha)
    {
        if (alpha < 0.01m) return 0.01m;
        if (alpha > 1m) return 1m;
        return alpha;
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

    private static Dictionary<string, long>? ParsePositiveUnitsDictionary(JsonElement obj)
    {
        var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Number &&
                p.Value.TryGetInt64(out var units) &&
                units > 0)
            {
                dict[p.Name] = units;
            }
        }
        return dict.Count > 0 ? dict : null;
    }
}
