using System.Diagnostics;
using System.Text;
using AgentsWinBar.Models;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services;

/// <summary>
/// Service for executing commands in WSL.
/// </summary>
public class WslService : IWslService
{
    private readonly ILogger<WslService> _logger;
    private readonly ISettingsService? _settingsService;
    private readonly string? _distro;
    private readonly IActivityLog? _activityLog;
    private readonly TimeProvider _timeProvider = TimeProvider.System;
    private readonly Func<string, CancellationToken, Task<WslResult>>? _commandExecutor;

    private TimeSpan Timeout => _settingsService != null
        ? ResolveCommandTimeout(_settingsService.Settings.Timeouts)
        : TimeSpan.FromSeconds(60);

    /// <summary>
    /// The hard limit for one WSL command. Callers such as <see cref="ICodexBarService"/> already enforce
    /// their own, provider-specific timeout through the cancellation token, so this cap must never be
    /// shorter than the longest of those: otherwise it fires first (as "Command timed out after 30 seconds")
    /// and the provider's own, longer, timeout never gets a chance to apply.
    /// </summary>
    public static TimeSpan ResolveCommandTimeout(TimeoutSettings timeouts)
    {
        var longestProviderTimeout = Math.Max(
            timeouts.CliProviderFirstFetchTimeoutSeconds,
            timeouts.CliProviderTimeoutSeconds);

        // Ordinary commands keep a minimum 60-second cap; cost scans supply their own longer timeout.
        return TimeSpan.FromSeconds(Math.Max(60, Math.Max(timeouts.WslCommandTimeoutSeconds, longestProviderTimeout)));
    }

    private TimeSpan StatusCheckTimeout => _settingsService != null
        ? TimeSpan.FromSeconds(_settingsService.Settings.Timeouts.WslStatusCheckTimeoutSeconds)
        : TimeSpan.FromSeconds(10);

    public WslService(ILogger<WslService> logger, ISettingsService? settingsService = null, string? distro = null,
        IActivityLog? activityLog = null)
    {
        _logger = logger;
        _settingsService = settingsService;
        _distro = distro;
        _activityLog = activityLog;
    }

    internal WslService(ILogger<WslService> logger, TimeProvider timeProvider,
        Func<string, CancellationToken, Task<WslResult>> commandExecutor) : this(logger)
    {
        _timeProvider = timeProvider;
        _commandExecutor = commandExecutor;
    }

    public Task<WslResult> ExecuteAsync(string command, CancellationToken ct = default) =>
        ExecuteAsync(command, Timeout, ct);

    public async Task<WslResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        _logger.LogDebug("Executing WSL command: {Command}", command);

        // bash -ic loads .bashrc which sets PATH
        var shellScript = command;

        var psi = new ProcessStartInfo
        {
            FileName = "wsl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Use ArgumentList for proper escaping
        if (_distro != null)
        {
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(_distro);
        }
        psi.ArgumentList.Add("--exec");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-ic");  // -i for interactive (loads .bashrc for Codex CLI)
        psi.ArgumentList.Add(shellScript);

        using var deadline = new CancellationTokenSource(timeout, _timeProvider);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        Process? process = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_commandExecutor != null) return await _commandExecutor(command, cts.Token);
            process = new Process { StartInfo = psi };
            process.Start();

            var budget = new WslOutputReader.Budget(WslOutputReader.CommandLimitBytes);
            var outputTask = ReadOutputAsync(process.StandardOutput, budget, cts);
            var errorTask = ReadOutputAsync(process.StandardError, budget, cts);
            try { await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(cts.Token)); }
            catch (Exception) when (OutputLimitExceeded(outputTask, errorTask))
            { return ExecutionFailure(new WslOutputLimitException()); }

            var output = await outputTask;
            var error = await errorTask;

            _logger.LogDebug("WSL command completed: {Command}, ExitCode={ExitCode}",
                command.Substring(0, Math.Min(50, command.Length)), process.ExitCode);

            return new WslResult
            {
                Success = process.ExitCode == 0,
                Output = output,
                Error = error,
                ExitCode = process.ExitCode
            };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("WSL command timed out after {Timeout}s: {Command}", timeout.TotalSeconds, command);
            return new WslResult
            {
                Success = false,
                Error = $"Command timed out after {timeout.TotalSeconds} seconds",
                ExitCode = -1
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute WSL command: {Command}", command);
            return ExecutionFailure(ex);
        }
        finally
        {
            // Always kill the process if still running to prevent zombie wslhost.exe
            KillProcessSafely(process);
            process?.Dispose();
        }
    }

    public async Task<IReadOnlyList<WslResult>> ExecuteBatchAsync(IReadOnlyList<WslBatchCommand> commands,
        Action<int, WslResult>? onCompleted = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0) return [];
        var marker = WslBatchProtocol.CreateMarker();
        var script = WslBatchProtocol.BuildScript(commands, marker);
        var results = new WslResult?[commands.Count];
        var psi = new ProcessStartInfo
        {
            FileName = "wsl", RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (_distro != null) { psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(_distro); }
        // --exec: without it wsl.exe hands the line to the default shell, which expands $VAR, $(...) and $((...))
        // in the script before the inner bash ever sees them.
        psi.ArgumentList.Add("--exec");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-ic");
        psi.ArgumentList.Add(script);

        // Individual jobs own their deadlines. This watchdog covers shell startup,
        // a broken framing utility, or other infrastructure that never terminates.
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(commands.Max(c => c.Timeout) + TimeSpan.FromSeconds(15));
        Process? process = null;
        Task? outputTask = null;
        Task<string>? errorTask = null;
        Exception? failure = null;
        try
        {
            process = new Process { StartInfo = psi };
            if (!process.Start()) throw new InvalidOperationException("WSL batch process did not start");
            var budget = new WslOutputReader.Budget(WslOutputReader.BatchLimitBytes);
            errorTask = ReadOutputAsync(process.StandardError, budget, watchdog);
            outputTask = WslBatchProtocol.ReadResultsAsync(process.StandardOutput, marker, results,
                onCompleted, watchdog.Token, budget);
            // Read while the jobs run, rather than waiting for the slowest provider.
            // A failed reader must also stop the process and its sibling reader.
            var first = await Task.WhenAny(outputTask, errorTask, process.WaitForExitAsync(watchdog.Token));
            if (first.IsFaulted) await first;
            await outputTask;
            await process.WaitForExitAsync(watchdog.Token);
            var error = await errorTask;
            ct.ThrowIfCancellationRequested();
            if (results.Any(r => r == null))
                throw new InvalidDataException($"WSL batch ended without all frames (exit {process.ExitCode}): {error.Trim()}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = OutputLimitExceeded(outputTask, errorTask) ? new WslOutputLimitException() : ex;
        }
        finally
        {
            KillProcessSafely(process);
            // Observe pending I/O after killing the process so cancellation and bad
            // frames cannot leave unobserved tasks or a live process behind.
            watchdog.Cancel();
            if (outputTask != null) { try { await outputTask; } catch { } }
            if (errorTask != null) { try { await errorTask; } catch { } }
            process?.Dispose();
        }

        ct.ThrowIfCancellationRequested();
        if (failure != null)
        {
            if (failure is WslOutputLimitException)
            {
                for (var i = 0; i < results.Length; i++)
                {
                    if (results[i] != null) continue;
                    results[i] = ExecutionFailure(failure);
                    onCompleted?.Invoke(i, results[i]!);
                }
                return results.Select(r => r!).ToArray();
            }
            _logger.LogWarning(failure, "WSL batch unavailable; retrying unfinished commands individually");
            _activityLog?.RecordRefresh(new DashboardRefreshEntry
            {
                TimestampUtc = DateTime.UtcNow, ProviderId = "wsl", Success = false,
                Message = "WSL batch fallback: " + failure.Message
            });
            await Task.WhenAll(Enumerable.Range(0, commands.Count).Where(i => results[i] == null).Select(async i =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(commands[i].Timeout);
                WslResult result;
                try
                {
                    result = await ExecuteAsync(commands[i].Command, commands[i].Timeout, deadline.Token);
                    ct.ThrowIfCancellationRequested();
                    if (deadline.IsCancellationRequested) result = TimedOutResult(result);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    result = TimedOutResult();
                }
                ct.ThrowIfCancellationRequested();
                results[i] = result;
                onCompleted?.Invoke(i, result);
            }));
        }
        return results.Select(r => r!).ToArray();
    }

    private static WslResult TimedOutResult(WslResult? result = null) => new()
    {
        Success = false, ExitCode = -1, Output = result?.Output ?? "", Error = "Command timed out"
    };

    private static bool OutputLimitExceeded(params Task?[] tasks) => tasks.Any(task =>
        task?.Exception?.Flatten().InnerExceptions.Any(ex => ex is WslOutputLimitException) == true);

    internal static async Task<string> ReadOutputAsync(TextReader reader, WslOutputReader.Budget budget,
        CancellationTokenSource cancellation)
    {
        try { return await WslOutputReader.ReadToEndAsync(reader, budget, cancellation.Token); }
        catch (WslOutputLimitException) { cancellation.Cancel(); throw; }
    }

    internal static WslResult ExecutionFailure(Exception exception) => new()
    {
        Success = false, Error = exception.Message,
        // A removed WSL executable must invalidate the cached setup verdict, just like a missing CLI.
        ExitCode = exception is System.ComponentModel.Win32Exception { NativeErrorCode: 2 } ? 127 : -1
    };

    public async Task<bool> IsWslInstalledAsync(CancellationToken ct = default)
    {
        var result = await CheckWslStatusAsync(ct);
        return result.IsInstalled;
    }

    public async Task<WslStatusResult> CheckWslStatusAsync(CancellationToken ct = default)
    {
        Process? process = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo
            {
                FileName = "wsl",
                Arguments = "--status",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process = Process.Start(psi);
            if (process == null)
            {
                return new WslStatusResult { IsInstalled = false, Error = Models.WslErrorType.NotInstalled };
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(StatusCheckTimeout);

            var budget = new WslOutputReader.Budget(WslOutputReader.CommandLimitBytes);
            var outputTask = ReadOutputAsync(process.StandardOutput, budget, cts);
            var errorTask = ReadOutputAsync(process.StandardError, budget, cts);
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(cts.Token));

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode == 0)
            {
                return new WslStatusResult { IsInstalled = true, IsRunning = true };
            }

            // Check for specific error conditions
            var combinedOutput = (output + error).ToLowerInvariant();
            if (combinedOutput.Contains("not running") || combinedOutput.Contains("起動していません"))
            {
                _logger.LogWarning("WSL is installed but not running");
                return new WslStatusResult
                {
                    IsInstalled = true,
                    IsRunning = false,
                    Error = Models.WslErrorType.NotRunning,
                    ErrorMessage = "WSL is not running. Please restart WSL."
                };
            }

            return new WslStatusResult
            {
                IsInstalled = true,
                IsRunning = false,
                Error = Models.WslErrorType.Other,
                ErrorMessage = error
            };
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            // File not found - WSL not installed
            _logger.LogDebug("WSL command not found");
            return new WslStatusResult { IsInstalled = false, Error = Models.WslErrorType.NotInstalled };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("WSL status check timed out");
            return new WslStatusResult
            {
                IsInstalled = true,
                IsRunning = false,
                Error = Models.WslErrorType.Timeout,
                ErrorMessage = "WSL status check timed out. WSL may be starting up."
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WSL is not installed or not accessible");
            return new WslStatusResult { IsInstalled = false, Error = Models.WslErrorType.NotInstalled };
        }
        finally
        {
            KillProcessSafely(process);
            process?.Dispose();
        }
    }

    public async Task<List<string>> GetDistrosAsync(CancellationToken ct = default)
    {
        Process? process = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo
            {
                FileName = "wsl",
                Arguments = "-l -q",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Unicode
            };

            process = Process.Start(psi);
            if (process == null) return [];

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(StatusCheckTimeout);

            var output = await ReadOutputAsync(process.StandardOutput,
                new WslOutputReader.Budget(WslOutputReader.CommandLimitBytes), cts);
            await process.WaitForExitAsync(cts.Token);

            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim('\0', ' ', '\r'))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get WSL distros");
            return [];
        }
        finally
        {
            KillProcessSafely(process);
            process?.Dispose();
        }
    }

    /// <summary>
    /// Safely kills a process and its entire process tree if still running.
    /// </summary>
    private void KillProcessSafely(Process? process)
    {
        if (process == null) return;

        try
        {
            if (!process.HasExited)
            {
                _logger.LogDebug("Killing WSL process {ProcessId}", process.Id);
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to kill WSL process");
        }
    }
}
