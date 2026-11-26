using System.Diagnostics;
using AgentsWinBar.Models;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services;

/// <summary>
/// Opens each provider's official sign-in, trying routes in order and falling back when a route is missing or fails
/// to start:
/// Claude: <c>claude auth login</c> in a terminal, else the Claude Code setup page.
/// Codex: <c>codex login</c> in a terminal, else the Codex CLI page.
/// Gemini: the Antigravity desktop app, else <c>agy</c> in a terminal (its first run asks for a Google sign-in),
/// else the Antigravity page.
/// The terminal is Windows Terminal when installed, else Command Prompt (<c>cmd.exe /k</c>, so the window stays open).
/// Every command line and URL is a constant: nothing is built from files, settings or network data.
/// </summary>
public sealed class SignInLauncher(ISignInSystem system, ILogger<SignInLauncher> logger) : ISignInLauncher
{
    // Confirmed in the tools' own local files: the Claude Code binary points to code.claude.com/docs/en/setup, the Codex
    // binary to developers.openai.com/codex/cli/, and the Antigravity app bundle to antigravity.google.
    internal const string ClaudeSetupUrl = "https://code.claude.com/docs/en/setup";
    internal const string CodexCliUrl = "https://developers.openai.com/codex/cli/";
    internal const string AntigravityUrl = "https://antigravity.google";

    internal const string ClaudeCommand = "claude auth login";
    internal const string CodexCommand = "codex login";
    /// <summary>agy has no login subcommand: its first interactive run asks for a Google sign-in.</summary>
    internal const string AgyCommand = "echo Sign in with Google when Antigravity asks, then close this window. & agy";

    /// <summary>The Antigravity desktop app, relative to %LOCALAPPDATA%.</summary>
    internal static readonly string AntigravityAppRelativePath = Path.Combine("Programs", "antigravity", "Antigravity.exe");

    public SignInLaunchResult Launch(string provider)
    {
        var id = ProviderConstants.ValidateAndNormalize(provider);
        foreach (var (route, process, description) in Routes(id))
        {
            try
            {
                system.Start(process);
                logger.LogInformation("Opened the {Provider} sign-in: {Route}", id, description);
                return new SignInLaunchResult(route, description);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not open the {Provider} sign-in via {Route}; trying the next way", id, description);
            }
        }
        logger.LogWarning("No way to open the {Provider} sign-in could be started", id);
        return new SignInLaunchResult(SignInRoute.None, "nothing could be started");
    }

    /// <summary>The routes for <paramref name="id"/> that are available on this machine, in the order to try them.</summary>
    internal IEnumerable<(SignInRoute Route, SignInProcess Process, string Description)> Routes(string id)
    {
        switch (id)
        {
            case "claude":
                if (system.FindOnPath("claude") != null)
                    foreach (var terminal in Terminal("Claude sign-in", ClaudeCommand)) yield return terminal;
                yield return Browser(ClaudeSetupUrl);
                break;
            case "codex":
                if (system.FindOnPath("codex") != null)
                    foreach (var terminal in Terminal("Codex sign-in", CodexCommand)) yield return terminal;
                yield return Browser(CodexCliUrl);
                break;
            case "gemini":
                var app = Path.Combine(system.LocalAppData, AntigravityAppRelativePath);
                if (system.FileExists(app))
                    yield return (SignInRoute.DesktopApp, new SignInProcess(app, string.Empty), "Antigravity app");
                if (system.FindOnPath("agy") != null)
                    foreach (var terminal in Terminal("Antigravity sign-in", AgyCommand)) yield return terminal;
                yield return Browser(AntigravityUrl);
                break;
        }
    }

    /// <summary>Windows Terminal (a new window) when installed, then Command Prompt.</summary>
    private IEnumerable<(SignInRoute, SignInProcess, string)> Terminal(string title, string command)
    {
        var cmd = Path.Combine(system.SystemDirectory, "cmd.exe");
        if (system.FindOnPath("wt") is { } wt)
            yield return (SignInRoute.WindowsTerminal,
                new SignInProcess(wt, $"-w new new-tab --title \"{title}\" \"{cmd}\" /k {command}", system.UserProfile),
                $"Windows Terminal: {command}");
        yield return (SignInRoute.CommandPrompt,
            new SignInProcess(cmd, $"/k title {title}& {command}", system.UserProfile), $"Command Prompt: {command}");
    }

    private static (SignInRoute, SignInProcess, string) Browser(string url) =>
        (SignInRoute.Browser, new SignInProcess(url, string.Empty), $"browser: {url}");
}

/// <summary>The real Windows behind <see cref="ISignInSystem"/>.</summary>
public sealed class WindowsSignInSystem : ISignInSystem
{
    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string SystemDirectory => Environment.SystemDirectory;

    public bool FileExists(string path) => File.Exists(path);

    public string? FindOnPath(string command)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(folder.Trim('"'), command + extension);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { break; }   // a malformed PATH entry
            }
        }
        return null;
    }

    public void Start(SignInProcess process)
    {
        var info = new ProcessStartInfo(process.FileName, process.Arguments) { UseShellExecute = true };
        if (process.WorkingDirectory is { Length: > 0 } folder && Directory.Exists(folder)) info.WorkingDirectory = folder;
        using var started = Process.Start(info);
    }
}
