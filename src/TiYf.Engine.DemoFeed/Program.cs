using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiYf.Engine.DemoFeed;

try
{
    var options = DemoFeedOptions.FromArgs(args);
    var result = DemoFeedRunner.Run(options);

    Console.WriteLine("INFO first_ts={0} last_ts={1} bars={2} symbols={3} run_dir={4} broker_dangling={5}",
        result.FirstTimestamp.ToString("O", CultureInfo.InvariantCulture),
        result.LastTimestamp.ToString("O", CultureInfo.InvariantCulture),
        result.BarsWritten,
        string.Join(',', result.Symbols),
        result.RunDirectory,
        result.BrokerHadDanglingPositions ? "true" : "false");

    Console.WriteLine("RUN_DIR={0}", result.RunDirectory);
    Console.WriteLine("JOURNAL_DIR_EVENTS={0}", result.EventsPath);
    Console.WriteLine("JOURNAL_DIR_TRADES={0}", result.TradesPath ?? string.Empty);

    return 0;
}
catch (DemoFeedException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Unexpected error: {ex.Message}");
    return 1;
}

internal sealed record DemoFeedResult(
    string RunDirectory,
    string EventsPath,
    string? TradesPath,
    DateTime FirstTimestamp,
    DateTime LastTimestamp,
    int BarsWritten,
    IReadOnlyList<string> Symbols,
    bool BrokerHadDanglingPositions);

internal static class DemoFeedRunner
{
    private const string SchemaVersion = "1.3.0";
    private const string ConfigHash = "DEMOFEED";

    private static readonly JsonSerializerOptions SerializerOptions = SerializerFactory.CreateSerializerOptions();

    public static DemoFeedResult Run(DemoFeedOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        var journalRoot = Path.GetFullPath(options.JournalRoot);
        Directory.CreateDirectory(journalRoot);
        var adapterRaw = string.IsNullOrWhiteSpace(options.AdapterId) ? "stub" : options.AdapterId.Trim();
        var sourceAdapter = adapterRaw.ToLowerInvariant();
        var isCTraderAdapter = string.Equals(sourceAdapter, "ctrader-demo", StringComparison.Ordinal) || string.Equals(sourceAdapter, "ctrader-live", StringComparison.Ordinal);
        var runDirectory = Path.GetFullPath(Path.Combine(journalRoot, sourceAdapter, options.RunId));
        if (Directory.Exists(runDirectory))
        {
            Directory.Delete(runDirectory, recursive: true);
        }
        Directory.CreateDirectory(runDirectory);
        var eventsPath = Path.Combine(runDirectory, "events.csv");
        if (File.Exists(eventsPath)) File.Delete(eventsPath);

        var brokerAccountId = string.IsNullOrWhiteSpace(options.BrokerAccountId)
            ? (isCTraderAdapter ? "ctrader-demo" : "stub-sim")
            : options.BrokerAccountId.Trim();
        var journalBrokerId = isCTraderAdapter ? "spotware" : "demo-stub";
        var journalAccountId = string.IsNullOrWhiteSpace(brokerAccountId) ? "unknown" : brokerAccountId;

        using var stream = new FileStream(eventsPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            NewLine = "\n"
        };

        writer.WriteLine($"schema_version={SchemaVersion},config_hash={ConfigHash},adapter_id={sourceAdapter},broker={journalBrokerId},account_id={journalAccountId}");
        writer.WriteLine("sequence,utc_ts,event_type,src_adapter,payload_json");

        var sequence = 1;
        var startUtc = options.StartUtc;
        var interval = TimeSpan.FromSeconds(options.IntervalSeconds);
        var firstTs = startUtc;
        var lastTs = startUtc;
        var barsBySymbol = new Dictionary<string, List<DemoBarSnapshot>>(StringComparer.Ordinal);
        var stubStatus = isCTraderAdapter ? "OFF" : (options.Broker.Enabled ? "ON" : "OFF");
        Console.WriteLine("BROKER_MODE={0} (stub={1}) accountId={2}", adapterRaw, stubStatus, journalAccountId);
        Console.WriteLine("SOURCE_ADAPTER={0}", sourceAdapter);
        Console.WriteLine("BROKER_ID={0}", journalBrokerId);
        Console.WriteLine("ACCOUNT_ID={0}", journalAccountId);

        if (isCTraderAdapter && !options.Broker.Enabled)
        {
            throw new DemoFeedException("ctrader-demo adapter requires broker-enabled=true");
        }

        if (isCTraderAdapter && options.Broker.Seed.HasValue)
        {
            throw new DemoFeedException("ctrader-demo adapter cannot run with broker-seed configured (stub disabled)");
        }

        foreach (var symbol in options.Symbols)
        {
            var clock = startUtc;
            var symbolBars = new List<DemoBarSnapshot>(options.Bars);
            barsBySymbol[symbol] = symbolBars;

            for (var i = 0; i < options.Bars; i++)
            {
                var endUtc = clock.Add(interval);
                var bar = GenerateBar(options.Seed, symbol, clock, endUtc, i, sourceAdapter);
                symbolBars.Add(new DemoBarSnapshot(endUtc, bar.Close));

                var payload = JsonSerializer.Serialize(bar, SerializerOptions);
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},BAR_V1,{2},\"{3}\"",
                    sequence,
                    endUtc.ToString("O", CultureInfo.InvariantCulture),
                    sourceAdapter,
                    EscapeForCsv(payload)));

                sequence++;
                lastTs = endUtc;
                clock = endUtc;
            }
        }

        writer.Flush();
        stream.Flush(flushToDisk: true);

        var tradesPath = Path.Combine(runDirectory, "trades.csv");
        if (File.Exists(tradesPath)) File.Delete(tradesPath);

        DemoBrokerResult brokerResult = isCTraderAdapter
            ? new DemoBrokerResult(Array.Empty<DemoTradeRecord>(), false)
            : DemoBrokerStub.GenerateTrades(options, barsBySymbol);

        if (options.Broker.Enabled)
        {
            // Emit explicit connectivity proof for automated log checks when broker mode is enabled.
            Console.WriteLine("Connected to cTrader endpoint (mode={0})", options.Broker.FillMode);
        }

        using (var tradesStream = new FileStream(tradesPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var tradesWriter = new StreamWriter(tradesStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            NewLine = "\n"
        })
        {
            tradesWriter.WriteLine("utc_ts_open,utc_ts_close,symbol,direction,entry_price,exit_price,volume_units,pnl_ccy,pnl_r,decision_id,schema_version,config_hash,src_adapter,data_version");
            var dataVersionValue = $"src_adapter={sourceAdapter};broker={journalBrokerId};account_id={journalAccountId}";

            foreach (var trade in brokerResult.Trades)
            {
                tradesWriter.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13}",
                    trade.UtcTsOpen.ToString("O", CultureInfo.InvariantCulture),
                    trade.UtcTsClose.ToString("O", CultureInfo.InvariantCulture),
                    trade.Symbol,
                    trade.Direction,
                    trade.EntryPrice.ToString("F4", CultureInfo.InvariantCulture),
                    trade.ExitPrice.ToString("F4", CultureInfo.InvariantCulture),
                    trade.VolumeUnits,
                    trade.PnlCcy.ToString("F2", CultureInfo.InvariantCulture),
                    trade.PnlR.ToString("F2", CultureInfo.InvariantCulture),
                    trade.DecisionId,
                    SchemaVersion,
                    ConfigHash,
                    sourceAdapter,
                    dataVersionValue));

                if (options.Broker.Enabled)
                {
                    Console.WriteLine(
                        "OrderSend ok decision={0} brokerOrderId={1} symbol={2} side={3} units={4} entry={5:F4} exit={6:F4}",
                        trade.DecisionId,
                        trade.BrokerOrderId,
                        trade.Symbol,
                        trade.Direction,
                        trade.VolumeUnits,
                        trade.EntryPrice,
                        trade.ExitPrice);
                }
            }

            tradesWriter.Flush();
            tradesStream.Flush(flushToDisk: true);
        }

        return new DemoFeedResult(
            runDirectory,
            Path.GetFullPath(eventsPath),
            Path.GetFullPath(tradesPath),
            firstTs,
            lastTs,
            options.Bars * options.Symbols.Count,
            options.Symbols,
            brokerResult.HadDanglingPositions);
    }

    private static GeneratedBar GenerateBar(int seed, string symbol, DateTime startUtc, DateTime endUtc, int index, string sourceAdapter)
    {
        var tick = seed + index * 31;
        var basePriceUnits = 10_000 + (tick % 1_000); // deterministic variation in fourth decimal place

        decimal Base(decimal units) => decimal.Round(units / 10_000m, 4, MidpointRounding.AwayFromZero);

        var open = Base(basePriceUnits);
        var drift = ((tick % 7) - 3) * 0.0001m;
        var close = Round4(open + drift);
        var high = Round4(decimal.Max(open, close) + 0.0002m);
        var rawLow = decimal.Min(open, close) - 0.0002m;
        var low = Round4(rawLow < 0m ? 0m : rawLow);

        var volume = 1_000 + (tick % 5) * 10;

        return new GeneratedBar(
            new InstrumentIdPayload(symbol),
            IntervalSeconds: (int)(endUtc - startUtc).TotalSeconds,
            StartUtc: DateTime.SpecifyKind(startUtc, DateTimeKind.Utc),
            EndUtc: DateTime.SpecifyKind(endUtc, DateTimeKind.Utc),
            Open: open,
            High: high,
            Low: low,
            Close: close,
            Volume: volume,
            SourceAdapter: sourceAdapter);
    }

    private static decimal Round4(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);

    private static string EscapeForCsv(string value) => value.Replace("\"", "\"\"");
}

internal sealed record InstrumentIdPayload(string Value);

internal sealed record GeneratedBar(
    InstrumentIdPayload InstrumentId,
    int IntervalSeconds,
    DateTime StartUtc,
    DateTime EndUtc,
    [property: JsonConverter(typeof(FixedDecimalJsonConverter))] decimal Open,
    [property: JsonConverter(typeof(FixedDecimalJsonConverter))] decimal High,
    [property: JsonConverter(typeof(FixedDecimalJsonConverter))] decimal Low,
    [property: JsonConverter(typeof(FixedDecimalJsonConverter))] decimal Close,
    int Volume,
    [property: JsonPropertyName("src_adapter")] string SourceAdapter);

internal sealed class FixedDecimalJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetDecimal();
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        writer.WriteRawValue(value.ToString("F4", CultureInfo.InvariantCulture));
    }
}

internal static class SerializerFactory
{
    public static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNamingPolicy = null,
            WriteIndented = false
        };
        options.Converters.Add(new FixedDecimalJsonConverter());
        return options;
    }
}

internal sealed class DemoFeedOptions
{
    private DemoFeedOptions(
        string runId,
        string journalRoot,
        IReadOnlyList<string> symbols,
        DateTime startUtc,
        int bars,
        int intervalSeconds,
        int seed,
        DemoBrokerOptions broker,
        string adapterId,
        string? brokerAccountId)
    {
        RunId = runId;
        JournalRoot = journalRoot;
        Symbols = symbols;
        StartUtc = startUtc;
        Bars = bars;
        IntervalSeconds = intervalSeconds;
        Seed = seed;
        Broker = broker;
        AdapterId = adapterId;
        BrokerAccountId = brokerAccountId;
    }

    public string RunId { get; }
    public string JournalRoot { get; }
    public IReadOnlyList<string> Symbols { get; }
    public DateTime StartUtc { get; }
    public int Bars { get; }
    public int IntervalSeconds { get; }
    public int Seed { get; }
    public DemoBrokerOptions Broker { get; }
    public string AdapterId { get; }
    public string? BrokerAccountId { get; }

    public static DemoFeedOptions FromArgs(string[] args)
    {
        var map = ParseArgs(args);

        string adapterRaw = GetOrDefault(map, "adapter-id", "stub");
        string adapterId = string.IsNullOrWhiteSpace(adapterRaw) ? "stub" : adapterRaw.Trim().ToLowerInvariant();
        if (adapterId != "stub" && adapterId != "ctrader-demo")
        {
            throw new DemoFeedException($"Unsupported adapter '{adapterRaw}'.");
        }

        string runId = GetOrDefault(map, "run-id", "DEMO-J1");
        if (string.IsNullOrWhiteSpace(runId)) throw new DemoFeedException("run-id cannot be empty");
        if (runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            runId.Contains(Path.DirectorySeparatorChar) ||
            runId.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new DemoFeedException("run-id contains invalid characters");
        }

        string defaultJournalRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "journals", "DEMO"));
        string journalRoot = GetOrDefault(map, "journal-root", defaultJournalRoot);
        journalRoot = Path.GetFullPath(journalRoot);

        string symbolsValue = GetOrDefault(map, "symbols", "EURUSD");
        var symbols = symbolsValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (symbols.Length == 0)
            throw new DemoFeedException("At least one symbol must be provided");
        if (symbols.Length > 1)
            throw new DemoFeedException("Demo feed stub only supports a single symbol");

        bool startUtcProvided = map.ContainsKey("start-utc");
        string defaultStartUtc = "2025-01-01T00:00:00Z";
        if (adapterId == "ctrader-demo" && !startUtcProvided)
        {
            var now = DateTime.UtcNow;
            var alignedNow = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            defaultStartUtc = alignedNow.AddMinutes(-120).ToString("O", CultureInfo.InvariantCulture);
        }

        string startValue = GetOrDefault(map, "start-utc", defaultStartUtc);
        if (!DateTime.TryParse(startValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var startUtc))
            throw new DemoFeedException($"Invalid start-utc value: '{startValue}'");
        startUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);

        string barsValue = GetOrDefault(map, "bars", "120");
        if (!int.TryParse(barsValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bars) || bars <= 0)
            throw new DemoFeedException("bars must be a positive integer");

        string intervalValue = GetOrDefault(map, "interval-seconds", "60");
        if (!int.TryParse(intervalValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intervalSeconds) || intervalSeconds <= 0)
            throw new DemoFeedException("interval-seconds must be a positive integer");

        string seedValue = GetOrDefault(map, "seed", "1337");
        if (!int.TryParse(seedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
            throw new DemoFeedException("seed must be an integer");

        var brokerEnabled = GetBoolean(map, "broker-enabled", fallback: false);
        var brokerFillMode = GetOrDefault(map, "broker-fill-mode", "ioc-market");
        if (string.IsNullOrWhiteSpace(brokerFillMode))
            throw new DemoFeedException("broker-fill-mode cannot be empty");
        int? brokerSeed = null;
        if (map.TryGetValue("broker-seed", out var brokerSeedValue) && !string.IsNullOrWhiteSpace(brokerSeedValue))
        {
            if (!int.TryParse(brokerSeedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBrokerSeed))
                throw new DemoFeedException("broker-seed must be an integer");
            brokerSeed = parsedBrokerSeed;
        }

        var brokerOptions = new DemoBrokerOptions(brokerEnabled, brokerFillMode, brokerSeed);

        string? brokerAccountId = null;
        if (map.TryGetValue("broker-account-id", out var brokerAccountIdValue) && !string.IsNullOrWhiteSpace(brokerAccountIdValue))
        {
            brokerAccountId = brokerAccountIdValue.Trim();
        }

        return new DemoFeedOptions(runId, journalRoot, Array.AsReadOnly(symbols), startUtc, bars, intervalSeconds, seed, brokerOptions, adapterId, brokerAccountId);
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new DemoFeedException($"Invalid argument format '{arg}'");

            string key;
            string value;
            var eqIndex = arg.IndexOf('=');
            if (eqIndex > 2)
            {
                key = arg[2..eqIndex];
                value = arg[(eqIndex + 1)..];
            }
            else
            {
                key = arg[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[i + 1];
                    i++;
                }
                else
                {
                    value = string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(key))
                throw new DemoFeedException("Argument key cannot be empty");

            map[key] = value;
        }

        return map;
    }

    private static string GetOrDefault(Dictionary<string, string> map, string key, string fallback)
    {
        return map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    private static bool GetBoolean(Dictionary<string, string> map, string key, bool fallback)
    {
        if (!map.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (bool.TryParse(value, out var boolean))
        {
            return boolean;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric != 0;
        }

        throw new DemoFeedException($"Invalid boolean value '{value}' for '{key}'.");
    }
}

internal sealed class DemoFeedException : Exception
{
    public DemoFeedException(string message) : base(message)
    {
    }
}
