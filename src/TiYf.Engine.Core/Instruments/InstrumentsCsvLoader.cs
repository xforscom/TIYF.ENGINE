using System.Globalization;

namespace TiYf.Engine.Core.Instruments;

public sealed class InstrumentsCsvFormatException : Exception
{
    public InstrumentsCsvFormatException(string message) : base(message) { }
}

public sealed record InstrumentSpec(
    string Symbol,
    string BaseCurrency,
    string QuoteCurrency,
    decimal PipSize,
    decimal TickSize,
    decimal ContractSize,
    long LotStep,
    int PriceDecimals,
    int VolumeDecimals,
    string TradingHours
);

public static class InstrumentsCsvLoader
{
    private static readonly string[] Required = new[] { "Symbol", "BaseCurrency", "QuoteCurrency", "PipSize", "TickSize", "ContractSize", "LotStep", "PriceDecimals", "VolumeDecimals", "TradingHours" };
    public static IReadOnlyList<InstrumentSpec> Load(string path)
    {
        if (!File.Exists(path)) throw new InstrumentsCsvFormatException($"Instruments file not found: {path}");
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) throw new InstrumentsCsvFormatException("Instruments CSV must contain header + at least one row");
        var headerParts = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headerParts.Length; i++) map[headerParts[i]] = i;
        foreach (var req in Required) if (!map.ContainsKey(req)) throw new InstrumentsCsvFormatException($"Missing required column '{req}'");
        var specs = new List<InstrumentSpec>();
        for (int row = 1; row < lines.Length; row++)
        {
            var line = lines[row]; if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            string GetS(string col) { return parts[map[col]].Trim(); }
            decimal GetD(string col) { if (!decimal.TryParse(GetS(col), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw new InstrumentsCsvFormatException($"Row {row}: invalid decimal for {col}='{GetS(col)}'"); return v; }
            int GetI(string col) { if (!int.TryParse(GetS(col), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) throw new InstrumentsCsvFormatException($"Row {row}: invalid int for {col}='{GetS(col)}'"); return v; }
            long GetL(string col) { if (!long.TryParse(GetS(col), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) throw new InstrumentsCsvFormatException($"Row {row}: invalid long for {col}='{GetS(col)}'"); return v; }
            var symbol = GetS("Symbol");
            if (string.IsNullOrWhiteSpace(symbol)) throw new InstrumentsCsvFormatException($"Row {row}: empty Symbol");
            var spec = new InstrumentSpec(
                symbol,
                GetS("BaseCurrency"),
                GetS("QuoteCurrency"),
                GetD("PipSize"),
                GetD("TickSize"),
                GetD("ContractSize"),
                GetL("LotStep"),
                GetI("PriceDecimals"),
                GetI("VolumeDecimals"),
                GetS("TradingHours")
            );
            // simple validations
            if (spec.PriceDecimals < 0 || spec.PriceDecimals > 10) throw new InstrumentsCsvFormatException($"Row {row}: PriceDecimals out of range {spec.PriceDecimals}");
            if (spec.LotStep < 1) throw new InstrumentsCsvFormatException($"Row {row}: LotStep must be >=1");
            if (spec.ContractSize <= 0) throw new InstrumentsCsvFormatException($"Row {row}: ContractSize must be >0");
            specs.Add(spec);
        }
        return specs;
    }
}
