using System.IO;
using System.Linq;
using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Host;

var parsed = ParseArgs(args);
var configPath = parsed.TryGetValue("--config", out var cfg) ? cfg : "proof/news.json";
var newsOverride = parsed.TryGetValue("--news", out var news) ? news : null;
var outputPath = parsed.TryGetValue("--output", out var output) ? output : "artifacts/m9-news-proof";
var nowRaw = parsed.TryGetValue("--now", out var now) ? now : null;

var (newsConfig, newsFile) = LoadConfig(configPath, newsOverride);
Directory.CreateDirectory(outputPath);
var events = LoadEvents(newsFile);
var defaultNowUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
var nowUtc = ParseOverride(nowRaw) ?? events.FirstOrDefault()?.Utc ?? defaultNowUtc;
var (windowStart, windowEnd) = ComputeBlackout(nowUtc, newsConfig, events);

var state = new EngineHostState("proof", Array.Empty<string>());
state.SetConfigSource(Path.GetFullPath(configPath), ConfigHash.Compute(File.ReadAllBytes(configPath)));
state.UpdateNewsTelemetry(events.LastOrDefault()?.Utc, events.Count, windowStart.HasValue, windowStart, windowEnd, newsConfig.SourceType);
var health = JsonSerializer.Serialize(state.CreateHealthPayload(), new JsonSerializerOptions { WriteIndented = true });

var metrics = BuildMetrics(events, windowStart, windowEnd, newsConfig.SourceType);
var summary = BuildSummary(events.Count, events.LastOrDefault()?.Utc, windowStart, windowEnd);

await File.WriteAllTextAsync(Path.Combine(outputPath, "summary.txt"), summary);
await File.WriteAllTextAsync(Path.Combine(outputPath, "metrics.txt"), metrics);
await File.WriteAllTextAsync(Path.Combine(outputPath, "health.json"), health);

Console.WriteLine(summary);

static Dictionary<string, string> ParseArgs(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length - 1; i++)
    {
        var key = args[i];
        if (!key.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }
        var value = args[i + 1];
        if (value.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }
        map[key] = value;
        i++;
    }
    return map;
}

static (NewsBlackoutConfig Config, string Path) LoadConfig(string configPath, string? overrideNews)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
    var root = doc.RootElement;
    if (!root.TryGetProperty("risk", out var riskNode))
    {
        throw new InvalidOperationException("Config missing risk block");
    }
    if (!riskNode.TryGetProperty("news_blackout", out var blackoutNode))
    {
        throw new InvalidOperationException("news_blackout block missing in config");
    }
    var enabled = blackoutNode.TryGetProperty("enabled", out var enabledProp) && enabledProp.GetBoolean();
    var before = blackoutNode.TryGetProperty("minutes_before", out var beforeProp) ? beforeProp.GetInt32() : 0;
    var after = blackoutNode.TryGetProperty("minutes_after", out var afterProp) ? afterProp.GetInt32() : 0;
    var source = overrideNews ?? (blackoutNode.TryGetProperty("source_path", out var srcProp) ? srcProp.GetString() : null);
    if (string.IsNullOrWhiteSpace(source))
    {
        throw new InvalidOperationException("news_blackout.source_path must be set or overridden");
    }
    var poll = blackoutNode.TryGetProperty("poll_seconds", out var pollProp) ? pollProp.GetInt32() : 60;
    var sourceType = blackoutNode.TryGetProperty("source_type", out var typeProp) ? typeProp.GetString() : null;
    var config = new NewsBlackoutConfig(enabled, before, after, source, poll, NewsSourceTypeHelper.Normalize(sourceType));
    var resolved = ResolveNewsPath(configPath, source);
    return (config, resolved);
}

static string ResolveNewsPath(string configPath, string source)
{
    if (Path.IsPathRooted(source))
    {
        return source;
    }
    var root = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Directory.GetCurrentDirectory();
    return Path.GetFullPath(Path.Combine(root, source));
}

static DateTime? ParseOverride(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }
    if (DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
    {
        return dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }
    return null;
}

static IReadOnlyList<NewsEvent> LoadEvents(string path)
{
    if (!File.Exists(path))
    {
        return Array.Empty<NewsEvent>();
    }

    var json = File.ReadAllText(path);
    using var doc = JsonDocument.Parse(json);
    if (doc.RootElement.ValueKind != JsonValueKind.Array)
    {
        return Array.Empty<NewsEvent>();
    }

    var events = new List<NewsEvent>();
    foreach (var element in doc.RootElement.EnumerateArray())
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            continue;
        }

        if (!element.TryGetProperty("utc", out var utcProp) || utcProp.ValueKind != JsonValueKind.String)
        {
            continue;
        }

        if (!DateTime.TryParse(utcProp.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var utc))
        {
            continue;
        }

        utc = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var impact = element.TryGetProperty("impact", out var impactProp) && impactProp.ValueKind == JsonValueKind.String
            ? impactProp.GetString() ?? string.Empty
            : string.Empty;
        var tags = element.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array
            ? tagsProp.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.String)
                .Select(t => t.GetString() ?? string.Empty)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .ToList()
            : new List<string>();
        events.Add(new NewsEvent(utc, impact, tags));
    }

    return events.OrderBy(e => e.Utc).ToList();
}

static (DateTime?, DateTime?) ComputeBlackout(DateTime nowUtc, NewsBlackoutConfig config, IReadOnlyList<NewsEvent> events)
{
    if (!config.Enabled)
    {
        return (null, null);
    }
    foreach (var ev in events)
    {
        var start = ev.Utc.AddMinutes(-config.MinutesBefore);
        var end = ev.Utc.AddMinutes(config.MinutesAfter);
        if (nowUtc >= start && nowUtc <= end)
        {
            return (start, end);
        }
    }
    return (null, null);
}

static string BuildSummary(int totalEvents, DateTime? lastEventUtc, DateTime? windowStart, DateTime? windowEnd)
{
    return $"news_summary events={totalEvents} last_event_utc={lastEventUtc?.ToString("O") ?? "n/a"} blackout_active={(windowStart.HasValue ? "true" : "false")} window_start={windowStart?.ToString("O") ?? "n/a"} window_end={windowEnd?.ToString("O") ?? "n/a"}";
}

static string BuildMetrics(IReadOnlyCollection<NewsEvent> events, DateTime? windowStart, DateTime? windowEnd, string? sourceType)
{
    var builder = new System.Text.StringBuilder();
    builder.AppendLine($"engine_news_events_fetched_total {events.Count}");
    builder.AppendLine($"engine_news_blackout_windows_total {(windowStart.HasValue && windowEnd.HasValue ? 1 : 0)}");
    var normalizedType = NewsSourceTypeHelper.Normalize(sourceType);
    builder.AppendLine($"engine_news_source{{type=\"{normalizedType}\"}} 1");
    if (events.LastOrDefault() is { } last)
    {
        var unix = new DateTimeOffset(last.Utc).ToUnixTimeSeconds();
        builder.AppendLine($"engine_news_last_event_ts {unix}");
    }
    return builder.ToString();
}
