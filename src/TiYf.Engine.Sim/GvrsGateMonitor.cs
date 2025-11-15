using System;
using System.Text.Json;
using TiYf.Engine.Core;
using TiYf.Engine.Core.Text;

namespace TiYf.Engine.Sim;

internal readonly record struct GvrsGateResult(RiskRailAlert Alert, bool Blocked);

internal sealed class GvrsGateMonitor
{
    private readonly GlobalVolatilityGateConfig _config;
    private readonly int? _maxBucketRank;
    private readonly decimal? _maxEwma;
    private readonly Action<DateTime>? _onBlock;

    public GvrsGateMonitor(GlobalVolatilityGateConfig config, Action<DateTime>? onBlock)
    {
        _config = config ?? GlobalVolatilityGateConfig.Disabled;
        _maxBucketRank = string.IsNullOrWhiteSpace(_config.LiveMaxBucket)
            ? null
            : BucketRank(_config.LiveMaxBucket!);
        _maxEwma = _config.LiveMaxEwma;
        _onBlock = onBlock;
    }

    public GvrsGateResult? Evaluate(string? bucket, decimal raw, decimal ewma, bool hasValue, string symbol, string timeframe, DateTime decisionUtc)
    {
        if (!_config.LiveModeEnabled || !hasValue)
        {
            return null;
        }

        if (decisionUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("GVRS gate decisions must be UTC", nameof(decisionUtc));
        }

        var normalizedBucket = BucketNormalizer.Normalize(bucket) ?? "unknown";
        var shouldBlockBucket = _maxBucketRank.HasValue && BucketRank(normalizedBucket) > _maxBucketRank.Value;
        var shouldBlockEwma = _maxEwma.HasValue && ewma > _maxEwma.Value;
        if (!(shouldBlockBucket || shouldBlockEwma))
        {
            return null;
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
            live_max_bucket = _config.LiveMaxBucket,
            live_max_ewma = _config.LiveMaxEwma
        });
        var alert = new RiskRailAlert("ALERT_BLOCK_GVRS_GATE", payload, false);
        return new GvrsGateResult(alert, Blocked: true);
    }

    private static int BucketRank(string? bucket)
    {
        var normalized = BucketNormalizer.Normalize(bucket)?.ToLowerInvariant();
        return normalized switch
        {
            "calm" => 0,
            "moderate" => 1,
            "volatile" => 2,
            _ => 3
        };
    }
}
