using System;
using TiYf.Engine.Core;
using TiYf.Engine.Sidecar;

namespace TiYf.Engine.Host;

internal sealed class HostJournalWriter : IJournalWriter
{
    private readonly FileJournalWriter _inner;
    private readonly Action<string> _onEvent;

    public HostJournalWriter(FileJournalWriter inner, Action<string> onEvent)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _onEvent = onEvent ?? (_ => { });
    }

    public string RunDirectory => _inner.RunDirectory;

    public async Task AppendAsync(JournalEvent evt, CancellationToken ct = default)
    {
        await _inner.AppendAsync(evt, ct).ConfigureAwait(false);
        _onEvent(evt.EventType);
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
