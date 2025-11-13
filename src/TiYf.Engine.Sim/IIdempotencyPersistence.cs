using System;
using System.Collections.Generic;

namespace TiYf.Engine.Sim;

public enum IdempotencyKind
{
    Order,
    Cancel
}

public readonly record struct IdempotencyEntry(string Key, DateTime TimestampUtc);

public readonly record struct IdempotencySnapshot(
    IReadOnlyList<IdempotencyEntry> Orders,
    IReadOnlyList<IdempotencyEntry> Cancels,
    int ExpiredDropped,
    DateTime LoadedUtc);

public interface IIdempotencyPersistence
{
    IdempotencySnapshot Load();
    void AddKey(IdempotencyKind kind, string key, DateTime timestampUtc);
    void RemoveKey(IdempotencyKind kind, string key);
}
