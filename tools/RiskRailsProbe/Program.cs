using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiYf.Engine.Core;
using TiYf.Engine.Core.Infrastructure;
using TiYf.Engine.Sim;

namespace RiskRailsProbe;

internal static class Program
{
    private const string DefaultTimeframeLabel = "H1";
    private const long DefaultUnits = 1000;

    private const decimal DefaultPerTradeRiskPct = 0.01m;
    private const decimal LargeCap = 100000m;
    private const decimal DefaultRealLeverageCap = 1000m;

    internal static int Main(string[] args)
    {
        try
        {
            var options = ArgumentOptions.Parse(args);

            var riskNode = BuildRiskJson(options);
            using var riskDoc = JsonDocument.Parse(riskNode.ToJsonString());
            var riskConfig = RiskConfigParser.Parse(riskDoc.RootElement);
            var canonical = JsonCanonicalizer.Canonicalize(riskDoc.RootElement);
            var configHash = ConfigHash.Compute(canonical);

            var newsEvents = LoadNewsEvents(options.NewsFile);

            var runtime = new RiskRailRuntime(
                riskConfig,
                configHash,
                newsEvents,
                gateCallback: null,
                startingEquity: options.EquityPeak ?? 100_000m);

            SeedRuntimeState(runtime, options);

            var outcome = runtime.EvaluateNewEntry(
                instrument: options.Instrument,
                timeframe: options.TimeframeLabel ?? DefaultTimeframeLabel,
                decisionUtc: options.DecisionTimestamp,
                requestedUnits: options.Units ?? DefaultUnits);

            if (outcome.Alerts.Count == 0)
            {
                Console.WriteLine("NO_ALERT");
                return 2;
            }

            WriteJournal(outcome.Alerts, options, configHash);
            Console.WriteLine(outcome.Alerts[0].EventType);
            return 0;
        }
        catch (ArgumentOptions.ArgumentOptionsException ex)
        {
            Console.Error.WriteLine($"Argument error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Probe failed:");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static JsonObject BuildRiskJson(ArgumentOptions options)
    {
        var risk = new JsonObject
        {
            ["per_trade_risk_pct"] = JsonValue.Create(DefaultPerTradeRiskPct),
            ["real_leverage_cap"] = JsonValue.Create(DefaultRealLeverageCap),
            ["per_position_risk_cap_pct"] = JsonValue.Create(LargeCap),
            ["basket_risk_cap_pct"] = JsonValue.Create(LargeCap),
            ["block_on_breach"] = JsonValue.Create(true),
            ["emit_evaluations"] = JsonValue.Create(true)
        };

        if (options.SessionWindow is not null)
        {
            var (start, end) = options.SessionWindow.Value;
            risk["session_window"] = new JsonObject
            {
                ["start_utc"] = start,
                ["end_utc"] = end
            };
        }

        if (options.HasDailyCap)
        {
            var daily = new JsonObject
            {
                ["action_on_breach"] = options.DailyCapAction ?? "block"
            };
            if (options.DailyLossThreshold.HasValue)
            {
                daily["loss"] = JsonValue.Create(options.DailyLossThreshold.Value);
            }
            if (options.DailyGainThreshold.HasValue)
            {
                daily["gain"] = JsonValue.Create(options.DailyGainThreshold.Value);
            }
            risk["daily_cap"] = daily;
        }

        if (options.DrawdownMax.HasValue)
        {
            risk["global_drawdown"] = new JsonObject
            {
                ["max_dd"] = JsonValue.Create(options.DrawdownMax.Value)
            };
        }

        if (!string.IsNullOrWhiteSpace(options.NewsFile))
        {
            risk["news_blackout"] = new JsonObject
            {
                ["enabled"] = JsonValue.Create(true),
                ["minutes_before"] = JsonValue.Create(options.NewsMinutesBefore ?? 0),
                ["minutes_after"] = JsonValue.Create(options.NewsMinutesAfter ?? 0),
                ["source_path"] = options.NewsFile
            };
        }

        return risk;
    }

    private static IReadOnlyList<NewsEvent> LoadNewsEvents(string? newsFile)
    {
        if (string.IsNullOrWhiteSpace(newsFile) || !File.Exists(newsFile))
        {
            return Array.Empty<NewsEvent>();
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(newsFile));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<NewsEvent>();
        }

        var events = new List<NewsEvent>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("utc", out var utcEl) || utcEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var ts = ParseUtc(utcEl.GetString()!);
            var impact = item.TryGetProperty("impact", out var impactEl) && impactEl.ValueKind == JsonValueKind.String
                ? impactEl.GetString() ?? string.Empty
                : string.Empty;

            var tags = new List<string>();
            if (item.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagsEl.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tag.GetString()))
                    {
                        tags.Add(tag.GetString()!.Trim());
                    }
                }
            }

            events.Add(new NewsEvent(ts, impact, tags));
        }

        return events;
    }

    private static void SeedRuntimeState(RiskRailRuntime runtime, ArgumentOptions options)
    {
        var runtimeType = typeof(RiskRailRuntime);

        void SetField(string name, object value)
        {
            var field = runtimeType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is null)
            {
                throw new InvalidOperationException($"Unable to locate field '{name}' on RiskRailRuntime.");
            }
            field.SetValue(runtime, value);
        }

        var pnl = options.PnlToday ?? 0m;
        SetField("_dailyRealizedPnl", pnl);
        SetField("_dailyUnrealizedPnl", 0m);
        SetField("_totalRealizedPnl", pnl);
        SetField("_totalUnrealizedPnl", 0m);

        if (options.EquityPeak.HasValue)
        {
            SetField("_equityPeak", options.EquityPeak.Value);
        }
        if (options.EquityNow.HasValue)
        {
            SetField("_currentEquity", options.EquityNow.Value);
        }
    }

    private static void WriteJournal(IReadOnlyList<RiskRailAlert> alerts, ArgumentOptions options, string configHash)
    {
        var outPath = options.OutputPath ?? throw new ArgumentNullException(nameof(options.OutputPath));
        var directory = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var writer = new StreamWriter(outPath, append: false, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine($"schema_version={Schema.Version},config_hash={configHash},adapter_id=probe,broker=probe,account_id=probe");
        writer.WriteLine("sequence,utc_ts,event_type,src_adapter,payload_json");

        var sequence = 1;
        foreach (var alert in alerts)
        {
            var payloadJson = JsonSerializer.Serialize(alert.Payload, new JsonSerializerOptions { WriteIndented = false });
            var escapedPayload = EscapeCsv(payloadJson);
            writer.WriteLine($"{sequence},{options.DecisionTimestamp:O},{alert.EventType},probe,{escapedPayload}");
            sequence++;
        }
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains('"') || value.Contains(','))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private static DateTime ParseUtc(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var result))
        {
            return DateTime.SpecifyKind(result, DateTimeKind.Utc);
        }
        throw new FormatException($"Invalid UTC timestamp '{value}'.");
    }

    private sealed class ArgumentOptions
    {
        public sealed class ArgumentOptionsException : Exception
        {
            public ArgumentOptionsException(string message) : base(message) { }
        }

        public DateTime DecisionTimestamp { get; private set; }
        public string Instrument { get; private set; } = string.Empty;
        public string? TimeframeLabel { get; private set; }
        public long? Units { get; private set; }
        public string? OutputPath { get; private set; }
        public (string Start, string End)? SessionWindow { get; private set; }
        public decimal? DailyLossThreshold { get; private set; }
        public decimal? DailyGainThreshold { get; private set; }
        public string? DailyCapAction { get; private set; }
        public decimal? DrawdownMax { get; private set; }
        public decimal? PnlToday { get; private set; }
        public decimal? EquityPeak { get; private set; }
        public decimal? EquityNow { get; private set; }
        public string? NewsFile { get; private set; }
        public int? NewsMinutesBefore { get; private set; }
        public int? NewsMinutesAfter { get; private set; }
        private bool DailyCapSpecified { get; set; }

        public bool HasDailyCap => DailyCapSpecified;

        public static ArgumentOptions Parse(string[] args)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                var key = arg[2..];
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                string value;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }
                else
                {
                    value = "true";
                }

                map[key] = value;
            }

            var opts = new ArgumentOptions
            {
                DecisionTimestamp = ParseUtc(GetRequired(map, "ts")),
                Instrument = GetRequired(map, "inst"),
                OutputPath = GetRequired(map, "out")
            };

            if (map.TryGetValue("timeframe", out var timeframe) && !string.IsNullOrWhiteSpace(timeframe))
            {
                opts.TimeframeLabel = timeframe.Trim();
            }
            if (map.TryGetValue("units", out var unitsRaw) && long.TryParse(unitsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var units))
            {
                opts.Units = Math.Max(1, units);
            }

            if (map.TryGetValue("session", out var sessionRaw) && !string.IsNullOrWhiteSpace(sessionRaw))
            {
                var parts = sessionRaw.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                {
                    throw new ArgumentOptionsException("Session window must be formatted as HH:mm[:ss]-HH:mm[:ss].");
                }
                opts.SessionWindow = (NormalizeTime(parts[0]), NormalizeTime(parts[1]));
            }

            if (map.TryGetValue("daily-loss", out var lossRaw) && decimal.TryParse(lossRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var loss))
            {
                opts.DailyLossThreshold = loss;
                opts.DailyCapSpecified = true;
            }
            if (map.TryGetValue("daily-gain", out var gainRaw) && decimal.TryParse(gainRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var gain))
            {
                opts.DailyGainThreshold = gain;
                opts.DailyCapSpecified = true;
            }
            if (map.TryGetValue("action", out var actionRaw) && !string.IsNullOrWhiteSpace(actionRaw))
            {
                opts.DailyCapAction = actionRaw.Trim().ToLowerInvariant();
                opts.DailyCapSpecified = true;
            }

            if (map.TryGetValue("dd", out var ddRaw) && decimal.TryParse(ddRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd))
            {
                opts.DrawdownMax = dd;
            }

            if (map.TryGetValue("pnl-today", out var pnlRaw) && decimal.TryParse(pnlRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var pnl))
            {
                opts.PnlToday = pnl;
            }

            if (map.TryGetValue("equity-peak", out var peakRaw) && decimal.TryParse(peakRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var peak))
            {
                opts.EquityPeak = peak;
            }
            if (map.TryGetValue("equity-now", out var nowRaw) && decimal.TryParse(nowRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var now))
            {
                opts.EquityNow = now;
            }

            if (map.TryGetValue("news-file", out var newsFile) && !string.IsNullOrWhiteSpace(newsFile))
            {
                opts.NewsFile = newsFile;
                if (map.TryGetValue("news-before", out var beforeRaw) && int.TryParse(beforeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var before))
                {
                    opts.NewsMinutesBefore = before;
                }
                if (map.TryGetValue("news-after", out var afterRaw) && int.TryParse(afterRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var after))
                {
                    opts.NewsMinutesAfter = after;
                }
            }

            if (opts.EquityPeak.HasValue && !opts.EquityNow.HasValue)
            {
                opts.EquityNow = opts.EquityPeak;
            }

            return opts;
        }

        private static string GetRequired(Dictionary<string, string> map, string key)
        {
            if (map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
            throw new ArgumentOptionsException($"Missing required argument --{key}.");
        }

        private static string NormalizeTime(string input)
        {
            var formats = new[] { @"HH\:mm", @"HH\:mm\:ss" };
            if (TimeSpan.TryParseExact(input, formats, CultureInfo.InvariantCulture, out var ts))
            {
                return ts.ToString(@"HH\:mm\:ss", CultureInfo.InvariantCulture);
            }
            throw new ArgumentOptionsException($"Invalid time format '{input}'. Use HH:mm or HH:mm:ss.");
        }
    }
}
