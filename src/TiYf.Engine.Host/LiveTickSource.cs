using System.Collections;
using System.Collections.Concurrent;
using TiYf.Engine.Core;
using TiYf.Engine.Sim;

namespace TiYf.Engine.Host;

internal sealed class LiveTickSource : ITickSource, IDisposable
{
    private readonly BlockingCollection<PriceTick> _queue = new(new ConcurrentQueue<PriceTick>());
    private bool _completed;

    public void Enqueue(PriceTick tick)
    {
        if (_queue.IsAddingCompleted) return;
        _queue.Add(tick);
    }

    public void Complete()
    {
        if (_completed) return;
        _completed = true;
        _queue.CompleteAdding();
    }

    public IEnumerator<PriceTick> GetEnumerator()
    {
        foreach (var tick in _queue.GetConsumingEnumerable())
        {
            yield return tick;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        Complete();
        _queue.Dispose();
    }
}
