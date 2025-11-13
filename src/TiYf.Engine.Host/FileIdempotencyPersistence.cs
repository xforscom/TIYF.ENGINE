using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TiYf.Engine.Sim;

namespace TiYf.Engine.Host;

internal sealed class FileIdempotencyPersistence : IIdempotencyPersistence
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly TimeSpan _ttl;
    private readonly int _orderCapacity;
    private readonly int _cancelCapacity;
    private readonly ILogger _logger;
    private readonly Func<DateTime> _clock;
    private bool _loaded;
    private Dictionary<string, DateTime> _orders = new(StringComparer.Ordinal);
    private Dictionary<string, DateTime> _cancels = new(StringComparer.Ordinal);
    private int _expiredDropped;
    private DateTime _lastLoadUtc;

    public FileIdempotencyPersistence(
        string path,
        TimeSpan ttl,
        int orderCapacity,
        int cancelCapacity,
        ILogger logger,
        Func<DateTime>? clock = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _ttl = ttl;
        _orderCapacity = orderCapacity;
        _cancelCapacity = cancelCapacity;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public IdempotencySnapshot Load()
    {
        lock (_sync)
        {
            if (!_loaded)
            {
                LoadUnsafe();
                _loaded = true;
                PersistUnsafe();
            }

            return CreateSnapshot(includeExpired: true);
        }
    }

    public void AddKey(IdempotencyKind kind, string key, DateTime timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_sync)
        {
            EnsureLoaded();
            var target = kind == IdempotencyKind.Order ? _orders : _cancels;
            target[key] = timestampUtc;
            TrimToCapacity(kind);
            PersistUnsafe();
        }
    }

    public void RemoveKey(IdempotencyKind kind, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_sync)
        {
            EnsureLoaded();
            var target = kind == IdempotencyKind.Order ? _orders : _cancels;
            if (target.Remove(key))
            {
                PersistUnsafe();
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        LoadUnsafe();
        _loaded = true;
    }

    private void LoadUnsafe()
    {
        _orders = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        _cancels = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        _expiredDropped = 0;
        _lastLoadUtc = _clock();

        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<PersistedEntry>(line);
                    if (entry is null || string.IsNullOrWhiteSpace(entry.Key))
                    {
                        continue;
                    }

                    var age = _lastLoadUtc - entry.TimestampUtc;
                    if (age > _ttl)
                    {
                        _expiredDropped++;
                        continue;
                    }

                    var target = entry.Kind == IdempotencyKind.Cancel ? _cancels : _orders;
                    target[entry.Key] = entry.TimestampUtc;
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse idempotency entry from persistence store.");
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to load idempotency persistence file path={Path}", _path);
        }

        TrimToCapacity(IdempotencyKind.Order);
        TrimToCapacity(IdempotencyKind.Cancel);
    }

    private void PersistUnsafe()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            foreach (var kvp in _orders.OrderBy(k => k.Value))
            {
                writer.WriteLine(JsonSerializer.Serialize(new PersistedEntry(IdempotencyKind.Order, kvp.Key, kvp.Value)));
            }
            foreach (var kvp in _cancels.OrderBy(k => k.Value))
            {
                writer.WriteLine(JsonSerializer.Serialize(new PersistedEntry(IdempotencyKind.Cancel, kvp.Key, kvp.Value)));
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to persist idempotency keys path={Path}", _path);
        }
    }

    private void TrimToCapacity(IdempotencyKind kind)
    {
        var target = kind == IdempotencyKind.Order ? _orders : _cancels;
        var capacity = kind == IdempotencyKind.Order ? _orderCapacity : _cancelCapacity;
        if (target.Count <= capacity)
        {
            return;
        }

        var overflow = target.Count - capacity;
        foreach (var key in target.OrderBy(k => k.Value).Select(k => k.Key).Take(overflow).ToArray())
        {
            target.Remove(key);
        }
    }

    private IdempotencySnapshot CreateSnapshot(bool includeExpired)
    {
        var orders = _orders.OrderBy(k => k.Value)
            .Select(k => new IdempotencyEntry(k.Key, k.Value))
            .ToArray();
        var cancels = _cancels.OrderBy(k => k.Value)
            .Select(k => new IdempotencyEntry(k.Key, k.Value))
            .ToArray();
        var expired = includeExpired ? _expiredDropped : 0;
        if (includeExpired)
        {
            _expiredDropped = 0;
        }
        return new IdempotencySnapshot(orders, cancels, expired, _lastLoadUtc);
    }

    private sealed record PersistedEntry(IdempotencyKind Kind, string Key, DateTime TimestampUtc);
}
