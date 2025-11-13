using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TiYf.Engine.Host;

internal sealed class StartupReconciliationRunner
{
    private readonly Func<DateTime> _clock;
    private readonly Func<DateTime, CancellationToken, Task> _emitAsync;
    private readonly ILogger _logger;
    private int _hasRun;

    public StartupReconciliationRunner(
        Func<DateTime> clock,
        Func<DateTime, CancellationToken, Task> emitAsync,
        ILogger logger)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _emitAsync = emitAsync ?? throw new ArgumentNullException(nameof(emitAsync));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _hasRun, 1) == 1)
        {
            return;
        }

        try
        {
            await _emitAsync(_clock(), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Startup reconciliation completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Startup reconciliation canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup reconciliation failed.");
        }
    }
}
