using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TiYf.Engine.Core;

public sealed class MarketContextService
{
    private readonly GlobalVolatilityGateConfig _config;
    private readonly decimal _ewmaAlpha;
    private readonly int _atrWindow;
    private readonly int _atrHistoryWindow;
    private readonly int _proxyWindow;

    private readonly Dictionary<string, FxAtrTracker> _fxTrackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProxyTracker> _proxyTrackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal> _latestFxValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal> _latestProxyValues = new(StringComparer.OrdinalIgnoreCase);

    private decimal _currentRaw;
    private decimal _currentEwma;
    private string _currentBucket = "unknown";
    private bool _hasValue;

    private static readonly string[] DefaultFxBasket =
    {
        "EURUSD", "GBPUSD", "USDJPY", "USDCHF", "USDCAD", "AUDUSD", "NZDUSD", "XAUUSD"
    };

    private static readonly string[] DefaultProxyBasket =
    {
        "SPX500_USD", "NAS100_USD", "XAUUSD"
    };

    public MarketContextService(
        GlobalVolatilityGateConfig config,
        int atrLookbackHours = 20 * 24,
        int atrPercentileHours = 252 * 24,
        int proxyLookbackHours = 14 * 24)
    {
        _config = config ?? GlobalVolatilityGateConfig.Disabled;
        _ewmaAlpha = ClampAlpha(_config.EwmaAlpha);
        _atrWindow = Math.Max(atrLookbackHours, 1);
        _atrHistoryWindow = Math.Max(atrPercentileHours, _atrWindow);
        _proxyWindow = Math.Max(proxyLookbackHours, 2);
    }

    public bool HasValue => _hasValue;

    public decimal CurrentRaw => _currentRaw;

    public decimal CurrentEwma => _currentEwma;

    public string CurrentBucket => _currentBucket;

    public string Mode => _config.EnabledMode ?? "disabled";

    public GvrsSnapshot Snapshot => new(_currentRaw, _currentEwma, _currentBucket, Mode, _hasValue);

    public GvrsEvaluation Evaluate(GlobalVolatilityGateConfig config)
    {
        var mode = config.EnabledMode ?? "disabled";
        if (!_hasValue)
        {
            return new GvrsEvaluation(false, _currentRaw, _currentEwma, "Unknown", mode);
        }

        var bucketTitle = _currentBucket switch
        {
            "calm" => "Calm",
            "volatile" => "Volatile",
            "moderate" => "Moderate",
            _ => "Unknown"
        };

        var shouldAlert = string.Equals(mode, "shadow", StringComparison.OrdinalIgnoreCase)
            && _currentEwma < config.EntryThreshold;

        return new GvrsEvaluation(shouldAlert, _currentRaw, _currentEwma, bucketTitle, mode);
    }

    public void OnBar(Bar bar, BarInterval interval)
    {
        if (!_config.IsEnabled) return;
        if (interval != BarInterval.OneHour) return;

        var symbol = bar.InstrumentId.Value;
        bool updated = false;

        if (IsFxBasketSymbol(symbol))
        {
            var tracker = GetOrCreateFxTracker(symbol);
            if (tracker.OnBar(bar) is { } normalized)
            {
                _latestFxValues[symbol] = normalized;
                updated = true;
            }
        }

        if (IsProxyBasketSymbol(symbol))
        {
            var tracker = GetOrCreateProxyTracker(symbol);
            if (tracker.OnBar(bar) is { } zNormalized)
            {
                _latestProxyValues[symbol] = zNormalized;
                updated = true;
            }
        }

        if (!updated) return;

        if (!TryComputeRaw(out var raw)) return;

        _currentRaw = Clamp(raw);
        if (!_hasValue)
        {
            _currentEwma = _currentRaw;
            _hasValue = true;
        }
        else
        {
            _currentEwma = (decimal)_ewmaAlpha * _currentRaw + (1m - (decimal)_ewmaAlpha) * _currentEwma;
        }

        _currentBucket = _currentEwma switch
        {
            <= -0.5m => "calm",
            >= 0.5m => "volatile",
            _ => "moderate"
        };
    }

    private FxAtrTracker GetOrCreateFxTracker(string symbol)
    {
        if (!_fxTrackers.TryGetValue(symbol, out var tracker))
        {
            tracker = new FxAtrTracker(_atrWindow, _atrHistoryWindow);
            _fxTrackers[symbol] = tracker;
        }
        return tracker;
    }

    private ProxyTracker GetOrCreateProxyTracker(string symbol)
    {
        if (!_proxyTrackers.TryGetValue(symbol, out var tracker))
        {
            tracker = new ProxyTracker(_proxyWindow);
            _proxyTrackers[symbol] = tracker;
        }
        return tracker;
    }

    private bool TryComputeRaw(out decimal raw)
    {
        decimal totalWeight = 0m;
        decimal weightedSum = 0m;

        foreach (var component in _config.EffectiveComponents)
        {
            if (string.IsNullOrWhiteSpace(component.Name) || component.Weight <= 0m) continue;
            var name = component.Name.Trim().ToLowerInvariant();
            decimal? value = name switch
            {
                "fx_atr_percentile" => TryAverage(_latestFxValues.Values, out var fxValue) ? fxValue : (decimal?)null,
                "risk_proxy_z" => TryAverage(_latestProxyValues.Values, out var proxyValue) ? proxyValue : (decimal?)null,
                _ => null
            };

            if (value is null) continue;

            weightedSum += value.Value * component.Weight;
            totalWeight += component.Weight;
        }

        if (totalWeight <= 0m)
        {
            raw = 0m;
            return false;
        }

        raw = weightedSum / totalWeight;
        return true;
    }

    private static bool TryAverage(IEnumerable<decimal> values, out decimal average)
    {
        var list = values?.ToArray();
        if (list is null || list.Length == 0)
        {
            average = 0m;
            return false;
        }
        average = list.Average();
        return true;
    }

    private static bool IsFxBasketSymbol(string symbol)
        => DefaultFxBasket.Contains(symbol, StringComparer.OrdinalIgnoreCase);

    private static bool IsProxyBasketSymbol(string symbol)
        => DefaultProxyBasket.Contains(symbol, StringComparer.OrdinalIgnoreCase);

    private static decimal ClampAlpha(decimal alpha)
    {
        if (alpha < 0.01m) return 0.01m;
        if (alpha > 1m) return 1m;
        return alpha;
    }

    private static decimal Clamp(decimal value)
    {
        if (value < -1m) return -1m;
        if (value > 1m) return 1m;
        return value;
    }

    public readonly record struct GvrsSnapshot(decimal Raw, decimal Ewma, string Bucket, string Mode, bool HasValue);
    public readonly record struct GvrsEvaluation(bool ShouldAlert, decimal Raw, decimal Ewma, string Bucket, string Mode);

    private sealed class FxAtrTracker
    {
        private readonly int _atrWindow;
        private readonly int _historyWindow;
        private readonly Queue<decimal> _trueRanges = new();
        private readonly Queue<decimal> _atrHistory = new();
        private decimal _trSum;
        private decimal? _previousClose;

        public FxAtrTracker(int atrWindow, int historyWindow)
        {
            _atrWindow = Math.Max(atrWindow, 1);
            _historyWindow = Math.Max(historyWindow, _atrWindow);
        }

        public decimal? OnBar(Bar bar)
        {
            var high = bar.High;
            var low = bar.Low;
            var close = bar.Close;

            decimal trueRange;
            if (_previousClose is { } prev)
            {
                var hl = high - low;
                var hc = Math.Abs(high - prev);
                var lc = Math.Abs(low - prev);
                trueRange = Math.Max(hl, Math.Max(hc, lc));
            }
            else
            {
                trueRange = high - low;
            }

            _previousClose = close;
            _trueRanges.Enqueue(trueRange);
            _trSum += trueRange;
            if (_trueRanges.Count > _atrWindow)
            {
                _trSum -= _trueRanges.Dequeue();
            }

            if (_trueRanges.Count < _atrWindow) return null;

            var atr = _trSum / _trueRanges.Count;
            _atrHistory.Enqueue(atr);
            if (_atrHistory.Count > _historyWindow)
            {
                _atrHistory.Dequeue();
            }

            if (_atrHistory.Count == 0) return null;

            var current = atr;
            var count = _atrHistory.Count;
            var lessOrEqual = _atrHistory.Count(v => v <= current);
            var percentile = (decimal)lessOrEqual / count;
            var normalized = (percentile * 2m) - 1m;
            return Clamp(normalized);
        }
    }

    private sealed class ProxyTracker
    {
        private readonly int _window;
        private readonly Queue<decimal> _returns = new();
        private decimal _sum;
        private decimal _sumSquares;
        private decimal? _previousClose;

        public ProxyTracker(int window)
        {
            _window = Math.Max(window, 2);
        }

        public decimal? OnBar(Bar bar)
        {
            if (bar.Close <= 0m) return null;
            decimal? output = null;
            if (_previousClose is { } prev && prev > 0m)
            {
                var ret = (bar.Close - prev) / prev;
                _returns.Enqueue(ret);
                _sum += ret;
                _sumSquares += ret * ret;
                if (_returns.Count > _window)
                {
                    var removed = _returns.Dequeue();
                    _sum -= removed;
                    _sumSquares -= removed * removed;
                }

                if (_returns.Count >= 2)
                {
                    var count = _returns.Count;
                    var mean = _sum / count;
                    var variance = (_sumSquares / count) - (mean * mean);
                    if (variance < 0m) variance = 0m;
                    var std = Math.Sqrt((double)variance);
                    if (std > 0d)
                    {
                        var z = (ret - mean) / (decimal)std;
                        var clamped = ClampZ(z);
                        output = Clamp(clamped / 3m);
                    }
                }
            }

            _previousClose = bar.Close;
            return output;
        }

        private static decimal ClampZ(decimal value)
        {
            if (value < -3m) return -3m;
            if (value > 3m) return 3m;
            return value;
        }
    }
}
