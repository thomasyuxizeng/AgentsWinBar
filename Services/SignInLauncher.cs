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
/// The CLI executable is resolved on PATH and invoked by its full path. Provider arguments and URLs are fixed.
/// </summary>
public sealed class SignInLauncher(ISignInSystem system, ILogger<SignInLauncher> logger) : ISignInLauncher
{
    // Confirmed in the tools' own local files: the Claude Code binary points to code.claude.com/docs/en/setup, the Codex
    // binary to developers.openai.com/codex/cli/, and the Antigravity app bundle to antigravity.google.
    internal const string ClaudeSetupUrl = "https://code.claude.com/docs/en/setup";
    internal const string CodexCliUrl = "https://developers.openai.com/codex/cli/";
    internal const string AntigravityUrl = "https://antigravity.google";

    /// <summary>agy has no login subcommand: its first interactive run asks for a Google sign-in.</summary>
    private const string AgyPrompt = "echo Sign in with Google when Antigravity asks, then close this window. & ";

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
                if (system.FindOnPath("claude") is { } claude)
                    foreach (var terminal in Terminal("Claude sign-in", claude, "auth login")) yield return terminal;
                yield return Browser(ClaudeSetupUrl);
                break;
            case "codex":
                if (system.FindOnPath("codex") is { } codex)
                    foreach (var terminal in Terminal("Codex sign-in", codex, "login")) yield return terminal;
                yield return Browser(CodexCliUrl);
                break;
            case "gemini":
                var app = Path.Combine(system.LocalAppData, AntigravityAppRelativePath);
                if (system.FileExists(app))
                    yield return (SignInRoute.DesktopApp, new SignInProcess(app, string.Empty), "Antigravity app");
                if (system.FindOnPath("agy") is { } agy)
                    foreach (var terminal in Terminal("Antigravity sign-in", agy, prompt: AgyPrompt)) yield return terminal;
                yield return Browser(AntigravityUrl);
                break;
        }
    }

    /// <summary>Windows Terminal (a new window) when installed, then Command Prompt.</summary>
    private IEnumerable<(SignInRoute, SignInProcess, string)> Terminal(
        string title, string executable, string arguments = "", string prompt = "")
    {
        // cmd expands percent expressions even inside quotes. Never pass such a path to it.
        if (!Path.IsPathFullyQualified(executable) || executable.AsSpan().ContainsAny('%', '"', '\r') || executable.Contains('\n'))
            yield break;

        var folder = system.SystemDirectory;
        var cmd = Path.Combine(folder, "cmd.exe");
        var command = arguments.Length == 0 ? $"\"{executable}\"" : $"\"{executable}\" {arguments}";
        // Starting with the title command keeps cmd from stripping the executable's quotes from the /k tail.
        var shellArgs = $"/d /v:off /k title {title}& {prompt}{command}";
        // Windows Terminal reparses arguments and uses semicolons to separate commands.
        // Use Command Prompt directly when a path needs quotes Windows Terminal may not preserve.
        if (!executable.Contains(';') &&
            (executable.Contains(' ') || executable.AsSpan().IndexOfAny("&()^") < 0) &&
            system.FindOnPath("wt") is { } wt)
        {
            yield return (SignInRoute.WindowsTerminal,
                new SignInProcess(wt, $"-w new new-tab --inheritEnvironment --title \"{title}\" --startingDirectory \"{folder}\" \"{cmd}\" {shellArgs}", folder),
                $"Windows Terminal: {title}");
        }
        yield return (SignInRoute.CommandPrompt, new SignInProcess(cmd, shellArgs, folder), $"Command Prompt: {title}");
    }

    private static (SignInRoute, SignInProcess, string) Browser(string url) =>
        (SignInRoute.Browser, new SignInProcess(url, string.Empty), $"browser: {url}");
}

/// <summary>The real Windows behind <see cref="ISignInSystem"/>.</summary>
public sealed class WindowsSignInSystem : ISignInSystem
{
    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string SystemDirectory => Environment.SystemDirectory;

    public bool FileExists(string path) => File.Exists(path);

    public string? FindOnPath(string command) =>
        FindOnPath(command, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"),
            FileExists, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), LocalAppData);

    internal static string? FindOnPath(string command, string? path, string? pathExtensions, Func<string, bool> exists,
        string? userProfile = null, string? localAppData = null)
    {
        var extensions = (pathExtensions ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in (path ?? string.Empty)
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folder = entry.Trim('"');
            if (!Path.IsPathFullyQualified(folder)) continue;
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(folder, command + extension);
                    if (exists(candidate)) return candidate;
                }
                catch (ArgumentException) { break; }   // a malformed PATH entry
            }
        }
        var fallback = command switch
        {
            "claude" when userProfile is not null && Path.IsPathFullyQualified(userProfile) =>
                Path.Combine(userProfile, ".local", "bin", "claude.exe"),
            "agy" when localAppData is not null && Path.IsPathFullyQualified(localAppData) =>
                Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "agy.exe"),
            _ => null
        };
        return fallback is not null && exists(fallback) ? fallback : null;
    }

    public void Start(SignInProcess process)
    {
        using var started = Process.Start(CreateStartInfo(process));
    }

    internal static ProcessStartInfo CreateStartInfo(SignInProcess process)
    {
        var info = new ProcessStartInfo(process.FileName, process.Arguments) { UseShellExecute = true };
        // Only terminal routes supply a working directory. Direct launch lets them inherit this setting.
        if (process.WorkingDirectory is { Length: > 0 } folder)
        {
            info.UseShellExecute = false;
            info.WorkingDirectory = folder;
            info.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        }
        return info;
    }
}
