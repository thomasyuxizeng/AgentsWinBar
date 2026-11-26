using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace AgentsWinBar.Services;

/// <summary>
/// Shows Windows notifications through the Windows App SDK. A packaged app needs the notification COM server
/// declared in Package.appxmanifest (windows.toastNotificationActivation plus windows.comServer with the same
/// CLSID); without it Register() throws and no notification can appear.
/// </summary>
public class ToastNotifier : IToastNotifier
{
    private readonly ILogger<ToastNotifier> _logger;
    private readonly object _gate = new();
    private bool _registered;
    private bool _unavailable;
    private bool _showFailureLogged;

    public ToastNotifier(ILogger<ToastNotifier> logger)
    {
        _logger = logger;
    }

    public ToastResult Show(string title, string message)
    {
        // Alerts arrive from the UI thread and early-reset toasts from the thread pool: register exactly once.
        lock (_gate)
        {
            if (_unavailable) return ToastResult.Unavailable;
            if (!_registered)
            {
                try
                {
                    // The handler must be attached before Register(); we don't act on clicks yet
                    AppNotificationManager.Default.NotificationInvoked += (_, _) => { };
                    AppNotificationManager.Default.Register();
                    _registered = true;
                }
                catch (Exception ex)
                {
                    // Running unpackaged, or the manifest lacks the COM server. Permanent for this process.
                    _unavailable = true;
                    _logger.LogWarning(ex, "Notifications are unavailable (0x{HResult:X8})", ex.HResult);
                    return ToastResult.Unavailable;
                }
            }
        }

        try
        {
            if (AppNotificationManager.Default.Setting != AppNotificationSetting.Enabled)
            {
                return ToastResult.TurnedOff;
            }

            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            return ToastResult.Shown;
        }
        catch (Exception ex)
        {
            // A single failed Show is not a reason to stop trying; log the first one only.
            if (!_showFailureLogged)
            {
                _showFailureLogged = true;
                _logger.LogWarning(ex, "A notification could not be shown (0x{HResult:X8})", ex.HResult);
            }

            return ToastResult.Unavailable;
        }
    }
}
