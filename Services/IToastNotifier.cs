namespace AgentsWinBar.Services;

/// <summary>What happened to a notification the app asked Windows to show.</summary>
public enum ToastResult
{
    /// <summary>Windows accepted it.</summary>
    Shown,

    /// <summary>Notifications are turned off for this app (or for all apps) in Windows Settings or by policy.</summary>
    TurnedOff,

    /// <summary>Windows refused to register or show it (for example when running unpackaged).</summary>
    Unavailable,
}

/// <summary>
/// Shows a Windows notification.
/// </summary>
public interface IToastNotifier
{
    /// <summary>
    /// Shows a toast with a title and a message. Never throws: when notifications are turned off or
    /// unavailable the call does nothing and says why.
    /// </summary>
    ToastResult Show(string title, string message);
}
