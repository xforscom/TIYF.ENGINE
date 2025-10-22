using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TiYf.Engine.Sim;

namespace TiYf.Engine.Host;

public sealed class EngineHostService : BackgroundService
{
    private readonly EngineHostState _state;
    private readonly IServiceProvider _services;
    private readonly ILogger<EngineHostService> _logger;
    private readonly TimeSpan _heartbeatInterval;
    private IConnectableExecutionAdapter? _executionAdapter;

    public EngineHostService(
        EngineHostState state,
        IServiceProvider services,
        ILogger<EngineHostService> logger,
        IOptions<EngineHostOptions>? options = null)
    {
        _state = state;
        _services = services;
        _logger = logger;
        var heartbeat = options?.Value.HeartbeatInterval ?? TimeSpan.FromSeconds(30);
        if (heartbeat <= TimeSpan.Zero)
        {
            heartbeat = TimeSpan.FromSeconds(30);
        }
        _heartbeatInterval = heartbeat;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _executionAdapter = _services.GetService<IConnectableExecutionAdapter>();
        var cTraderSettings = _services.GetService<CTraderAdapterSettings>();
        var oandaSettings = _services.GetService<OandaAdapterSettings>();

        if (cTraderSettings is not null)
        {
            _logger.LogInformation(
                "host: adapter meta adapter={Adapter} broker={Broker} account={Account} mode={Mode}",
                _state.Adapter,
                cTraderSettings.Broker,
                string.IsNullOrWhiteSpace(cTraderSettings.AccountId) ? "unknown" : cTraderSettings.AccountId,
                cTraderSettings.Mode);
        }
        else if (oandaSettings is not null)
        {
            _logger.LogInformation(
                "host: adapter meta adapter={Adapter} broker=OANDA account={Account} mode={Mode}",
                _state.Adapter,
                string.IsNullOrWhiteSpace(oandaSettings.AccountId) ? "unknown" : oandaSettings.AccountId,
                oandaSettings.Mode);
        }
        else
        {
            _logger.LogInformation(
                "host: adapter meta adapter={Adapter} broker=demo-stub account=stub-sim mode=stub",
                _state.Adapter);
        }

        _logger.LogInformation("EngineHostService starting (adapter={Adapter})", _state.Adapter);

        if (_executionAdapter != null)
        {
            try
            {
                await _executionAdapter.ConnectAsync(stoppingToken);
                _state.MarkConnected(true);
                var brokerMode = cTraderSettings is not null ? "ctrader" : oandaSettings is not null ? "oanda" : "stub";
                _logger.LogInformation("host: connected adapter={Adapter} broker_mode={Mode}", _state.Adapter, brokerMode);
            }
            catch (Exception ex)
            {
                _state.MarkConnected(false);
                var adapterLabel = cTraderSettings is not null ? "cTrader" : oandaSettings is not null ? "OANDA" : "adapter";
                _logger.LogError(ex, "{AdapterLabel} handshake failed", adapterLabel);
            }
        }
        else
        {
            _state.MarkConnected(true);
            _logger.LogInformation("host: connected adapter={Adapter} broker_mode=stub", _state.Adapter);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            _state.Beat();
            var now = DateTime.UtcNow.ToString("O");
            var lastDecision = _state.LastH1DecisionUtc?.ToString("O") ?? "none";
            _logger.LogInformation(
                "host: heartbeat t={Timestamp} adapter={Adapter} connected={Connected} last_h1_decision={LastDecision} pending_orders={Pending} bar_lag_ms={Lag}",
                now,
                _state.Adapter,
                _state.Connected ? "true" : "false",
                lastDecision,
                _state.PendingOrders,
                _state.BarLagMilliseconds.ToString(CultureInfo.InvariantCulture));
            try
            {
                await Task.Delay(_heartbeatInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("EngineHostService stopping");
        if (_executionAdapter is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }
}
