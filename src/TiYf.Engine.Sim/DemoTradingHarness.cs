using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TiYf.Engine.Core;
using TiYf.Engine.Core.Instruments;
using TiYf.Engine.Sidecar;
using TiYf.Engine.Tools;

namespace TiYf.Engine.Sim;

/// <summary>
/// Options controlling a demo trading execution.
/// </summary>
public sealed record DemoTradingOptions(
    string ConfigPath,
    string? RunId = null,
    string? JournalRoot = null,
    decimal SlippageBps = 0m,
    TimeSpan? FillLatency = null,
    string BarEventType = "BAR_V1",
    string? ArtifactRoot = null,
    bool ZipArtifacts = true,
    string MinimumSchema = "1.3.0"
);

/// <summary>
/// Result summary emitted by the demo trading harness.
/// </summary>
public sealed record DemoTradingResult(
    string RunId,
    string JournalDirectory,
    string EventsPath,
    string TradesPath,
    string StrictReportPath,
    string ParityReportPath,
    string EnvSanityPath,
    string? ZipPath,
    string ConfigHash,
    string SchemaVersion,
    string? DataVersion,
    int AlertBlockCount,
    int TradeRowCount,
    int StrictExitCode,
    int ParityExitCode
);

/// <summary>
/// Execution adapter used by the demo harness. Applies deterministic fills with configurable slippage.
/// </summary>
public sealed class DemoBrokerSimulator : IExecutionAdapter
{
    private readonly TickBook _book;
    private readonly decimal _slippageFraction;
    private readonly TimeSpan _latency;
    private readonly IReadOnlyDictionary<string, int> _priceDecimals;

    public DemoBrokerSimulator(TickBook book, decimal slippageBps, TimeSpan latency, IReadOnlyDictionary<string, int> priceDecimals)
    {
        _book = book;
        _slippageFraction = slippageBps == 0m ? 0m : slippageBps / 10_000m;
        _latency = latency;
        _priceDecimals = priceDecimals;
    }

    public Task<ExecutionResult> ExecuteMarketAsync(OrderRequest order, CancellationToken ct = default)
    {
        var (bid, ask) = _book.Get(order.Symbol, order.UtcTs);
        decimal price = order.Side == TradeSide.Buy ? ask : bid;

        if (_slippageFraction != 0m)
        {
            var slipFactor = order.Side == TradeSide.Buy ? 1m + _slippageFraction : 1m - _slippageFraction;
            price = decimal.Round(price * slipFactor, 10, MidpointRounding.AwayFromZero);
        }

        if (!_priceDecimals.TryGetValue(order.Symbol, out var decimals)) decimals = 5;
        price = decimal.Round(price, decimals, MidpointRounding.AwayFromZero);

        var fillTs = _latency == TimeSpan.Zero ? order.UtcTs : AlignToMinute(order.UtcTs + _latency);
        var fill = new ExecutionFill(order.DecisionId, order.Symbol, order.Side, price, order.Units, fillTs);
        return Task.FromResult(new ExecutionResult(true, string.Empty, fill));
    }

    private static DateTime AlignToMinute(DateTime ts)
    {
        ts = DateTime.SpecifyKind(ts, DateTimeKind.Utc);
        return new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, ts.Minute, 0, DateTimeKind.Utc);
    }
}

internal static class DemoTickBookBuilder
{
    public static TickBook Build(JsonDocument raw, string configDirectory)
    {
        var rows = new List<(string Symbol, DateTime Ts, decimal Bid, decimal Ask)>();
        if (!raw.RootElement.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Demo config missing data section");
        if (!dataNode.TryGetProperty("ticks", out var ticksNode) || ticksNode.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Demo config requires data.ticks map");

        foreach (var entry in ticksNode.EnumerateObject().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var relPath = entry.Value.GetString();
            if (string.IsNullOrWhiteSpace(relPath)) continue;
            var path = ResolvePath(configDirectory, relPath);
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length < 4) continue;
                var ts = DateTime.Parse(parts[0], null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                var bid = decimal.Parse(parts[1], CultureInfo.InvariantCulture);
                var ask = decimal.Parse(parts[2], CultureInfo.InvariantCulture);
                rows.Add((entry.Name, DateTime.SpecifyKind(ts, DateTimeKind.Utc), bid, ask));
            }
        }
        if (rows.Count == 0) throw new InvalidOperationException("No ticks loaded for demo harness.");
        return new TickBook(rows);
    }

    public static IReadOnlyDictionary<string, string> ResolveTickPaths(JsonDocument raw, string configDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!raw.RootElement.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
            return result;
        if (!dataNode.TryGetProperty("ticks", out var ticksNode) || ticksNode.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var entry in ticksNode.EnumerateObject())
        {
            var rel = entry.Value.GetString();
            if (string.IsNullOrWhiteSpace(rel)) continue;
            var path = ResolvePath(configDirectory, rel);
            result[entry.Name] = path;
        }
        return result;
    }

    private static string ResolvePath(string baseDir, string relative)
    {
        if (Path.IsPathRooted(relative)) return Path.GetFullPath(relative);
        return Path.GetFullPath(Path.Combine(baseDir, relative));
    }
}

public static class DemoTradingHarness
{
    public static async Task<DemoTradingResult> RunAsync(DemoTradingOptions options, CancellationToken ct = default)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        var configPath = Path.GetFullPath(options.ConfigPath);
        if (!File.Exists(configPath)) throw new FileNotFoundException("Demo config not found", configPath);

        var (config, configHash, rawConfig) = EngineConfigLoader.Load(configPath);
        var configDir = Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory();

        var tickPaths = DemoTickBookBuilder.ResolveTickPaths(rawConfig, configDir);
        if (tickPaths.Count == 0)
            throw new InvalidOperationException("Demo harness requires tick fixtures defined under data.ticks");

        var timestamps = AggregateTimestamps(tickPaths.Values);
        if (timestamps.Count == 0)
            throw new InvalidOperationException("Demo harness could not aggregate timestamps from tick fixtures.");

        var instruments = LoadInstruments(rawConfig, configDir);
        if (instruments.Count == 0)
            throw new InvalidOperationException("Demo harness requires at least one instrument in instrumentsFile");
        var catalog = new InMemoryInstrumentCatalog(instruments.Select(i => new Instrument(new InstrumentId(i.Symbol), i.Symbol, i.PriceDecimals)));

        var runId = ResolveRunId(options.RunId, config.RunId, rawConfig, timestamps[0]);
        var journalRoot = ResolveJournalRoot(options.JournalRoot, config.JournalRoot, rawConfig, configDir);
        Directory.CreateDirectory(journalRoot);
        var runDir = Path.Combine(journalRoot, runId);
        if (Directory.Exists(runDir)) Directory.Delete(runDir, true);

        var dataVersion = ComputeDataVersion(rawConfig, configDir);
        var barIntervals = DetermineIntervals(config);
        var builders = CreateBuilders(catalog.All().Select(i => i.Id), barIntervals);
        var barTracker = new InMemoryBarKeyTracker();
        var clock = new DeterministicSequenceClock(timestamps);
        var tickSource = new MultiInstrumentTickSource(rawConfig);
        var tickBook = DemoTickBookBuilder.Build(rawConfig, configDir);

        var priceDecimals = instruments.ToDictionary(i => i.Symbol, i => i.PriceDecimals, StringComparer.OrdinalIgnoreCase);
        var execution = new DemoBrokerSimulator(
            tickBook,
            options.SlippageBps,
            options.FillLatency ?? TimeSpan.Zero,
            priceDecimals);
        var positions = new PositionTracker();

        await using var journalWriter = new FileJournalWriter(journalRoot, runId, config.SchemaVersion, configHash, dataVersion);
        await using var tradesWriter = new TradesJournalWriter(journalRoot, runId, config.SchemaVersion, configHash, dataVersion);

        var strategy = new DeterministicScriptStrategy(clock, catalog.All(), timestamps[0]);
        var loop = new EngineLoop(
            clock,
            builders,
            barTracker,
            journalWriter,
            tickSource,
            options.BarEventType,
            riskFormulas: new RiskFormulas(),
            basketAggregator: new BasketRiskAggregator(),
            configHash: configHash,
            schemaVersion: config.SchemaVersion,
            deterministicStrategy: strategy,
            execution: execution,
            positions: positions,
            tradesWriter: tradesWriter,
            dataVersion: dataVersion,
            riskMode: "shadow");

        await loop.RunAsync(ct);

        // Ensure writers flush before post-processing
        await tradesWriter.DisposeAsync();
        await journalWriter.DisposeAsync();

        var eventsPath = Path.Combine(runDir, "events.csv");
        var tradesPath = Path.Combine(runDir, "trades.csv");
        if (!File.Exists(eventsPath) || !File.Exists(tradesPath))
            throw new InvalidOperationException("Demo harness expected events and trades journals to be present after run.");

        var strictReport = StrictJournalVerifier.Verify(new StrictVerifyRequest(eventsPath, tradesPath, options.MinimumSchema, strict: true));
        var strictPath = Path.Combine(runDir, "strict.json");
        File.WriteAllText(strictPath, strictReport.JsonReport, new UTF8Encoding(false));

        var parity = ParitySnapshot.Compute(eventsPath, eventsPath, tradesPath, tradesPath);
        var parityPath = Path.Combine(runDir, "parity.json");
        File.WriteAllText(parityPath, JsonSerializer.Serialize(parity, new JsonSerializerOptions { WriteIndented = false }), new UTF8Encoding(false));

        var alertCount = CountAlerts(eventsPath);
        var tradeRows = CountTradeRows(tradesPath);
        var envPath = Path.Combine(runDir, "env.sanity");
        WriteEnvSanity(envPath, new EnvSnapshot(
            runId,
            configPath,
            journalRoot,
            eventsPath,
            tradesPath,
            configHash,
            dataVersion,
            strictReport.ExitCode,
            parity.ExitCode,
            tradeRows,
            alertCount,
            parity.Events.HashA,
            parity.Trades?.HashA));

        string? zipPath = null;
        if (options.ZipArtifacts)
        {
            var artifactRoot = !string.IsNullOrWhiteSpace(options.ArtifactRoot)
                ? Path.GetFullPath(options.ArtifactRoot)
                : journalRoot;
            Directory.CreateDirectory(artifactRoot);
            zipPath = Path.Combine(artifactRoot, $"demo-trading-{runId}.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(runDir, zipPath, CompressionLevel.SmallestSize, includeBaseDirectory: false);
        }

        return new DemoTradingResult(
            runId,
            runDir,
            eventsPath,
            tradesPath,
            strictPath,
            parityPath,
            envPath,
            zipPath,
            configHash,
            config.SchemaVersion,
            dataVersion,
            alertCount,
            tradeRows,
            strictReport.ExitCode,
            parity.ExitCode);
    }

    private static IReadOnlyList<InstrumentSpec> LoadInstruments(JsonDocument rawConfig, string configDir)
    {
        if (!rawConfig.RootElement.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
            return Array.Empty<InstrumentSpec>();
        if (!dataNode.TryGetProperty("instrumentsFile", out var instNode) || instNode.ValueKind != JsonValueKind.String)
            return Array.Empty<InstrumentSpec>();
        var path = instNode.GetString();
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<InstrumentSpec>();
        var resolved = Path.IsPathRooted(path) ? path : Path.Combine(configDir, path);
        return InstrumentsCsvLoader.Load(resolved);
    }

    private static List<DateTime> AggregateTimestamps(IEnumerable<string> tickPaths)
    {
        var set = new ConcurrentDictionary<DateTime, byte>();
        Parallel.ForEach(tickPaths, path =>
        {
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length < 1) continue;
                if (!DateTime.TryParse(parts[0], null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts))
                    continue;
                set.TryAdd(DateTime.SpecifyKind(ts, DateTimeKind.Utc), 0);
            }
        });
        return set.Keys.OrderBy(d => d).ToList();
    }

    private static IReadOnlyCollection<BarInterval> DetermineIntervals(EngineConfig config)
    {
        static BarInterval Map(string code) => code.ToUpperInvariant() switch
        {
            "M1" => BarInterval.OneMinute,
            "H1" => BarInterval.OneHour,
            "D1" => BarInterval.OneDay,
            _ => throw new ArgumentException($"Unsupported interval '{code}' for demo harness")
        };

        if (config.Intervals is { Length: > 0 })
            return config.Intervals.Select(Map).Distinct().ToArray();
        return new[] { BarInterval.OneMinute };
    }

    private static Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder> CreateBuilders(IEnumerable<InstrumentId> instruments, IEnumerable<BarInterval> intervals)
    {
        var builders = new Dictionary<(InstrumentId, BarInterval), IntervalBarBuilder>();
        foreach (var inst in instruments)
        {
            foreach (var interval in intervals)
            {
                builders[(inst, interval)] = new IntervalBarBuilder(interval);
            }
        }
        return builders;
    }

    private static string ResolveRunId(string? requestedRunId, string configRunId, JsonDocument rawConfig, DateTime firstTimestamp)
    {
        if (!string.IsNullOrWhiteSpace(requestedRunId))
            return FormatRunId(requestedRunId);

        if (!string.IsNullOrWhiteSpace(configRunId))
            return FormatRunId(configRunId);

        var suffix = firstTimestamp.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        return $"DEMO-RUN-{suffix}";

        static string FormatRunId(string raw)
        {
            if (raw.StartsWith("M0-RUN", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("DEMO-RUN", StringComparison.OrdinalIgnoreCase))
                return raw;
            return $"DEMO-RUN-{raw}";
        }
    }

    private static string ResolveJournalRoot(string? requestedRoot, string configRoot, JsonDocument rawConfig, string configDir)
    {
        if (!string.IsNullOrWhiteSpace(requestedRoot))
            return Path.GetFullPath(requestedRoot);

        if (!string.IsNullOrWhiteSpace(configRoot))
            return Path.IsPathRooted(configRoot) ? configRoot : Path.GetFullPath(Path.Combine(configDir, configRoot));

        return Path.Combine(configDir, "journals", "M0");
    }

    private static string? ComputeDataVersion(JsonDocument rawConfig, string configDir)
    {
        try
        {
            if (!rawConfig.RootElement.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
                return null;
            var paths = new List<string>();
            if (dataNode.TryGetProperty("instrumentsFile", out var instNode) && instNode.ValueKind == JsonValueKind.String)
            {
                var instPath = instNode.GetString();
                if (!string.IsNullOrWhiteSpace(instPath))
                {
                    var resolved = Path.IsPathRooted(instPath) ? instPath : Path.Combine(configDir, instPath);
                    if (File.Exists(resolved)) paths.Add(resolved);
                }
            }
            if (dataNode.TryGetProperty("ticks", out var ticksNode) && ticksNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in ticksNode.EnumerateObject())
                {
                    var rel = entry.Value.GetString();
                    if (string.IsNullOrWhiteSpace(rel)) continue;
                    var resolved = Path.IsPathRooted(rel) ? rel : Path.Combine(configDir, rel);
                    if (File.Exists(resolved)) paths.Add(resolved);
                }
            }
            return paths.Count > 0 ? DataVersion.Compute(paths) : null;
        }
        catch
        {
            return null;
        }
    }

    private static int CountAlerts(string eventsPath)
    {
        return File.ReadLines(eventsPath)
            .Count(line => line.StartsWith("ALERT_BLOCK_", StringComparison.Ordinal));
    }

    private static int CountTradeRows(string tradesPath)
    {
        return Math.Max(0, File.ReadLines(tradesPath).Skip(1).Count());
    }

    private static void WriteEnvSanity(string path, EnvSnapshot snapshot)
    {
        var lines = new List<string>
        {
            $"run_id={snapshot.RunId}",
            $"config={snapshot.ConfigPath}",
            $"journal_root={snapshot.JournalRoot}",
            "sim_exit=0",
            $"strict_exit={snapshot.StrictExit}",
            $"parity_exit={snapshot.ParityExit}",
            $"events_path={snapshot.EventsPath}",
            $"trades_path={snapshot.TradesPath}",
            $"trades_row_count={snapshot.TradeRowCount}",
            $"alert_block_count={snapshot.AlertBlockCount}",
            $"config_hash={snapshot.ConfigHash}",
            $"data_version={snapshot.DataVersion ?? string.Empty}",
            $"events_sha={snapshot.EventsSha}",
            $"trades_sha={snapshot.TradesSha ?? string.Empty}"
        };
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private sealed record EnvSnapshot(
        string RunId,
        string ConfigPath,
        string JournalRoot,
        string EventsPath,
        string TradesPath,
        string ConfigHash,
        string? DataVersion,
        int StrictExit,
        int ParityExit,
        int TradeRowCount,
        int AlertBlockCount,
        string EventsSha,
        string? TradesSha
    );
}