using System.Diagnostics;

namespace AgentsWinBar.Services.Native;

/// <summary>
/// The outcome of one <c>claude auth status --json</c> run. It deliberately carries no output: the command prints the
/// account email, which the app must never log or keep.
/// </summary>
public readonly record struct ClaudeAuthStatusRun(int? ExitCode, TimeSpan Duration, bool TimedOut);

/// <summary>
/// Starts Claude Code's own <c>claude auth status --json</c>, which renews an expired Claude Code sign-in as a side
/// effect (Claude Code rewrites its own credential file; the app never writes it). Behind an interface so the client's
/// renewal logic is testable without running Claude Code.
/// </summary>
public interface IClaudeSignInRenewer
{
    /// <summary>The Claude Code executable, or null when it is not installed.</summary>
    string? FindClaudeExecutable();

    /// <summary>Whether only an npm command shim is available for Claude Code.</summary>
    bool HasNpmShimOnly();

    /// <summary>
    /// Runs <c>&lt;executable&gt; auth status --json</c> hidden with its output discarded. Kills the process tree after
    /// <paramref name="timeout"/> or on cancellation. Never throws for a process that runs and fails.
    /// </summary>
    Task<ClaudeAuthStatusRun> RunAuthStatusAsync(string executable, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Runs the real <c>claude</c> command. No other Claude Code command is ever run.</summary>
public sealed class ClaudeAuthStatusRunner : IClaudeSignInRenewer
{
    private const string ExecutableName = "claude.exe";
    private static readonly string[] AuthStatusArguments = ["auth", "status", "--json"];
    private readonly IReadOnlyList<string> _arguments;
    private readonly Func<string?> _readPath;
    private readonly Func<string> _userProfile;

    public ClaudeAuthStatusRunner() : this(AuthStatusArguments, null, null)
    {
    }

    /// <summary>Test constructor: other arguments (to run a harmless stand-in) and a fake PATH and profile.</summary>
    internal ClaudeAuthStatusRunner(IReadOnlyList<string> arguments, Func<string?>? readPath, Func<string>? userProfile)
    {
        _arguments = arguments;
        _readPath = readPath ?? (() => Environment.GetEnvironmentVariable("PATH"));
        _userProfile = userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>
    /// claude.exe in an absolute PATH folder (relative entries, which would resolve against the working folder, are
    /// ignored), else Claude Code's native install at %USERPROFILE%\.local\bin\claude.exe.
    /// </summary>
    public string? FindClaudeExecutable()
    {
        var executable = FindOnPath(ExecutableName);
        if (executable != null) return executable;
        var profile = _userProfile();
        if (string.IsNullOrEmpty(profile) || !Path.IsPathFullyQualified(profile)) return null;
        var native = Path.Combine(profile, ".local", "bin", ExecutableName);
        return File.Exists(native) ? native : null;
    }

    public bool HasNpmShimOnly() => FindOnPath("claude.cmd") != null && FindClaudeExecutable() == null;

    private string? FindOnPath(string fileName)
    {
        foreach (var entry in (_readPath() ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var folder = entry.Trim('"');
                if (!Path.IsPathFullyQualified(folder)) continue;
                var candidate = Path.Combine(folder, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped.
            }
        }
        return null;
    }

    public async Task<ClaudeAuthStatusRun> RunAuthStatusAsync(string executable, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath()
        };
        foreach (var argument in _arguments) info.ArgumentList.Add(argument);

        var started = Stopwatch.GetTimestamp();
        using var process = new Process { StartInfo = info };
        using var stopOnShutdown = RenewalProcess.Start(process, ct);
        // Nothing is typed into it, and everything it prints (the account email among it) is thrown away unread.
        process.StandardInput.Close();
        var drainOutput = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        var drainError = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RenewalProcess.KillTree(process);
            await DrainQuietlyAsync(drainOutput, drainError).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new(null, Stopwatch.GetElapsedTime(started), TimedOut: true);
        }
        await DrainQuietlyAsync(drainOutput, drainError).ConfigureAwait(false);
        return new(process.ExitCode, Stopwatch.GetElapsedTime(started), TimedOut: false);
    }

    private static async Task DrainQuietlyAsync(Task output, Task error)
    {
        try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or OperationCanceledException)
        {
            // The pipes close with the process; a straggler is abandoned.
        }
    }
}
