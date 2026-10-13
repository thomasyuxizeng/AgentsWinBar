using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

public sealed class BatterySaverMonitor : IBatterySaverMonitor, IDisposable
{
    // Windows.System.Power.EnergySaverStatus.On = 2; all other values are inactive.
    private const int EnergySaverOn = 2;
    private readonly IBatterySaverStatusSource _source;
    private readonly ILogger<BatterySaverMonitor> _logger;
    private bool _disposed;

    public bool BatterySaverActive { get; private set; }
    public event EventHandler? BatterySaverChanged;

    public BatterySaverMonitor(IBatterySaverStatusSource source, ILogger<BatterySaverMonitor> logger)
    {
        _source = source;
        _logger = logger;
        try { _source.EnergySaverStatusChanged += OnStatusChanged; }
        catch (Exception ex) { _logger.LogWarning(ex, "Battery saver notifications unavailable"); }
        BatterySaverActive = ReadActive();
    }

    private bool ReadActive()
    {
        try { return _source.EnergySaverStatus == EnergySaverOn; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Battery saver state unavailable");
            return false;
        }
    }

    private void OnStatusChanged(object? sender, object args)
    {
        try
        {
            if (_disposed) return;
            var active = ReadActive();
            if (active == BatterySaverActive) return;
            BatterySaverActive = active;
            BatterySaverChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { _logger.LogError(ex, "Battery saver change handler failed"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _source.EnergySaverStatusChanged -= OnStatusChanged; }
        catch (Exception ex) { _logger.LogWarning(ex, "Battery saver notifications could not be detached"); }
    }
}
