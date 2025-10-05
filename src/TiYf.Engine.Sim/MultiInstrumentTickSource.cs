using System.Text.Json;
using TiYf.Engine.Core;

namespace TiYf.Engine.Sim;

public sealed class MultiInstrumentTickSource : ITickSource
{
    private readonly List<PriceTick> _ticks = new();
    public MultiInstrumentTickSource(JsonDocument raw)
    {
        var data = raw.RootElement.GetProperty("data");
        var ticksNode = data.GetProperty("ticks");
        foreach (var kv in ticksNode.EnumerateObject())
        {
            var sym = kv.Name;
            var path = kv.Value.GetString();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var instId = new InstrumentId(sym);
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(','); // timestamp_utc,bid,ask,volume
                if (parts.Length < 4) continue;
                var ts = DateTime.Parse(parts[0], null, System.Globalization.DateTimeStyles.AssumeUniversal|System.Globalization.DateTimeStyles.AdjustToUniversal);
                var bid = decimal.Parse(parts[1]);
                var ask = decimal.Parse(parts[2]);
                var mid = (bid + ask)/2m; // mid for bar building (bars use close etc.)
                var vol = decimal.Parse(parts[3]);
                _ticks.Add(new PriceTick(instId, ts, mid, vol));
            }
        }
        _ticks = _ticks.OrderBy(t=>t.UtcTimestamp).ThenBy(t=>t.InstrumentId.Value).ToList();
    }
    public IEnumerator<PriceTick> GetEnumerator() => _ticks.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
