namespace AgentsWinBar.Services;

/// <summary>Which way <see cref="ISignInLauncher.Launch"/> opened the provider's sign-in.</summary>
public enum SignInRoute
{
    /// <summary>Nothing could be started.</summary>
    None,
    /// <summary>A Windows Terminal window running the provider's CLI.</summary>
    WindowsTerminal,
    /// <summary>A Command Prompt window running the provider's CLI (Windows Terminal is not installed).</summary>
    CommandPrompt,
    /// <summary>The provider's desktop app (Antigravity for Gemini).</summary>
    DesktopApp,
    /// <summary>The provider's setup page in the default browser.</summary>
    Browser
}

/// <summary>The route used and a short description for the log ("Windows Terminal: claude auth login").</summary>
public sealed record SignInLaunchResult(SignInRoute Route, string Description)
{
    public bool Started => Route != SignInRoute.None;
}

/// <summary>
/// Opens a provider's official sign-in: its CLI's login command in a terminal, its desktop app, or its setup page.
/// The app never signs in itself; the provider's own tool writes the sign-in the app then reads.
/// </summary>
public interface ISignInLauncher
{
    /// <summary>Opens the sign-in for <paramref name="provider"/>. Throws for an unknown provider.</summary>
    SignInLaunchResult Launch(string provider);
}

/// <summary>One process to start: an executable with fixed arguments, or a URL opened by the shell.</summary>
public sealed record SignInProcess(string FileName, string Arguments, string? WorkingDirectory = null);

/// <summary>The bits of Windows the launcher touches, injectable so tests never start anything.</summary>
public interface ISignInSystem
{
    /// <summary>Full path of <paramref name="command"/> found on PATH (with PATHEXT), or null.</summary>
    string? FindOnPath(string command);
    bool FileExists(string path);
    /// <summary>%LOCALAPPDATA%.</summary>
    string LocalAppData { get; }
    /// <summary>%USERPROFILE%, the terminal's working folder.</summary>
    string UserProfile { get; }
    /// <summary>C:\Windows\System32.</summary>
    string SystemDirectory { get; }
    /// <summary>Starts <paramref name="process"/> through the shell. Throws when it cannot be started.</summary>
    void Start(SignInProcess process);
}
