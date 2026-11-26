namespace AgentsWinBar.Services;

/// <summary>
/// Shows a Windows notification.
/// </summary>
public interface IToastNotifier
{
    /// <summary>
    /// Shows a toast with a title and a message. Never throws: if notifications aren't available
    /// (for example when running unpackaged) the call is silently ignored.
    /// </summary>
    void Show(string title, string message);
}
