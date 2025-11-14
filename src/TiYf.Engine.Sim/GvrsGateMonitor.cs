using System;
using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Core.Text;

namespace TiYf.Engine.Sim;

internal readonly record struct GvrsGateResult(RiskRailAlert Alert, bool Blocked);

internal sealed class GvrsGateMonitor
{
    private readonly bool _enabled;
    private readonly bool _blockOnVolatile;
    private readonly Action<DateTime>? _onBlock;

    public GvrsGateMonitor(bool enabled, bool blockOnVolatile, Action<DateTime>? onBlock)
    {
        _enabled = enabled;
        _blockOnVolatile = blockOnVolatile;
        _onBlock = onBlock;
    }

    public GvrsGateResult? Evaluate(string? bucket, decimal raw, decimal ewma, string symbol, string timeframe, DateTime decisionUtc)
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
            blocking_enabled = _blockOnVolatile
        });
        var alert = new RiskRailAlert("ALERT_BLOCK_GVRS_GATE", payload, false);
        var blocked = _blockOnVolatile;
        return new GvrsGateResult(alert, blocked);
    }
}
