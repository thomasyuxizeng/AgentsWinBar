using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace AgentsWinBar.Services;

/// <summary>
/// Shows Windows notifications through the Windows App SDK (needs the app to be packaged).
/// </summary>
public class ToastNotifier : IToastNotifier
{
    private readonly ILogger<ToastNotifier> _logger;
    private bool _registered;
    private bool _unavailable;

    public ToastNotifier(ILogger<ToastNotifier> logger)
    {
        _logger = logger;
    }

    public void Show(string title, string message)
    {
        if (_unavailable)
        {
            return;
        }

        try
        {
            if (!_registered)
            {
                // The handler must be attached before Register(); we don't act on clicks yet
                AppNotificationManager.Default.NotificationInvoked += (_, _) => { };
                AppNotificationManager.Default.Register();
                _registered = true;
            }

            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            // Typically: running unpackaged, or notifications blocked. Don't keep retrying.
            _unavailable = true;
            _logger.LogWarning(ex, "Notifications are unavailable");
        }
    }
}
