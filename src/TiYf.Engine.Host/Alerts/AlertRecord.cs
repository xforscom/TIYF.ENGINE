using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TiYf.Engine.Host.Alerts;

public sealed record AlertRecord(
    string Category,
    string Severity,
    string Summary,
    string? Details,
    DateTime OccurredUtc,
    IReadOnlyDictionary<string, string>? Properties = null);

public interface IAlertSink
{
    void Enqueue(AlertRecord alert);
}
