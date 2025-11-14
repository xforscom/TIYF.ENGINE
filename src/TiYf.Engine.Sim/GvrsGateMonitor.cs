using System;
using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Core.Text;

namespace TiYf.Engine.Sim;

internal sealed class GvrsGateMonitor
{
    private readonly bool _enabled;
    private readonly Action<DateTime>? _onBlock;

    public GvrsGateMonitor(bool enabled, Action<DateTime>? onBlock)
    {
        _enabled = enabled;
        _onBlock = onBlock;
    }

    public RiskRailAlert? TryCreateAlert(string? bucket, decimal raw, decimal ewma, string symbol, string timeframe, DateTime decisionUtc)
    {
        if (!_enabled)
        {
            return null;
        }

        var normalizedBucket = BucketNormalizer.Normalize(bucket);
        if (!string.Equals(normalizedBucket, "volatile", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (decisionUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("GVRS gate decisions must be UTC", nameof(decisionUtc));
        }

        _onBlock?.Invoke(decisionUtc);
        var payload = JsonSerializer.SerializeToElement(new
        {
            instrument = symbol,
            timeframe,
            ts = decisionUtc,
            gvrs_bucket = normalizedBucket,
            gvrs_raw = raw,
            gvrs_ewma = ewma,
            blocking_enabled = true
        });
        return new RiskRailAlert("ALERT_BLOCK_GVRS_GATE", payload, false);
    }
}
