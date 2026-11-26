using System.Diagnostics;

namespace AgentsWinBar.Services.Native;

/// <summary>
/// The outcome of one <c>agy models</c> run. It deliberately carries no output: nothing agy prints is kept or logged.
/// </summary>
public readonly record struct AgyModelsRun(int? ExitCode, TimeSpan Duration, bool TimedOut);

/// <summary>
/// Starts agy's own <c>agy models</c> (it lists models and uses none), which renews an expired Antigravity sign-in as a
/// side effect (agy rewrites its own credential; the app never writes it). Behind an interface so the client's renewal
/// logic is testable without running agy.
/// </summary>
public interface IAgySignInRenewer
{
    /// <summary>The agy executable, or null when it is not installed.</summary>
    string? FindAgyExecutable();

    /// <summary>
    /// Runs <c>&lt;executable&gt; models</c> hidden with its output discarded. Kills the process tree after
    /// <paramref name="timeout"/> or on cancellation. Never throws for a process that runs and fails.
    /// </summary>
    Task<AgyModelsRun> RunModelsAsync(string executable, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Runs the real <c>agy models</c> command. No other agy command is ever run.</summary>
public sealed class AgyModelsRunner : IAgySignInRenewer
{
    private const string ExecutableName = "agy.exe";
    private static readonly string[] ModelsArguments = ["models"];
    private readonly IReadOnlyList<string> _arguments;
    private readonly Func<string?> _readPath;
    private readonly Func<string?> _localAppData;

    public AgyModelsRunner() : this(ModelsArguments, null, null)
    {
    }

    /// <summary>Test constructor: other arguments (to run a harmless stand-in) and a fake PATH and local app data folder.</summary>
    internal AgyModelsRunner(IReadOnlyList<string> arguments, Func<string?>? readPath, Func<string?>? localAppData)
    {
        _arguments = arguments;
        _readPath = readPath ?? (() => Environment.GetEnvironmentVariable("PATH"));
        _localAppData = localAppData ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    /// <summary>
    /// agy.exe in an absolute PATH folder (relative entries, which would resolve against the working folder, are
    /// ignored), else the WinGet link %LOCALAPPDATA%\Microsoft\WinGet\Links\agy.exe (agy installs through WinGet).
    /// </summary>
    public string? FindAgyExecutable()
    {
        foreach (var entry in (_readPath() ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var folder = entry.Trim('"');
                if (!Path.IsPathFullyQualified(folder)) continue;
                var candidate = Path.Combine(folder, ExecutableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped.
            }
        }
        var localAppData = _localAppData();
        if (string.IsNullOrEmpty(localAppData) || !Path.IsPathFullyQualified(localAppData)) return null;
        var winGetLink = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", ExecutableName);
        return File.Exists(winGetLink) ? winGetLink : null;
    }

    public async Task<AgyModelsRun> RunModelsAsync(string executable, TimeSpan timeout, CancellationToken ct)
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
        using var process = Process.Start(info) ?? throw new InvalidOperationException("agy did not start.");
        // Nothing is typed into it, and everything it prints is thrown away unread.
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
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // It exited on its own in the meantime, or cannot be killed; either way nothing more can be done.
            }
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
