using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CodexBarWin.Services;

/// <summary>Forwards Windows session transitions; subscribers choose their execution context.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SessionStateSource : ISessionStateSource, IDisposable
{
    private readonly ILogger<SessionStateSource> _logger;
    private bool _disposed;

    public bool IsLocked => SessionStateNative.IsCurrentSessionLocked();

    public event EventHandler? Locked;
    public event EventHandler? Unlocked;
    public event EventHandler? Resumed;

    public SessionStateSource(ILogger<SessionStateSource> logger)
    {
        _logger = logger;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        try
        {
            if (_disposed) return;
            // Rare, and the only trace of whether SystemEvents reaches the app at all (it has not in testing)
            _logger.LogInformation("Session switch event: {Reason}", e.Reason);
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                case SessionSwitchReason.RemoteDisconnect:
                case SessionSwitchReason.ConsoleDisconnect:
                    Locked?.Invoke(this, EventArgs.Empty);
                    break;
                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.RemoteConnect:
                case SessionSwitchReason.ConsoleConnect:
                    Unlocked?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Session transition handler failed"); }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        try
        {
            if (!_disposed && e.Mode == PowerModes.Resume) Resumed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { _logger.LogError(ex, "Resume transition handler failed"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
