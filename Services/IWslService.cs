using CodexBarWin.Models;

namespace CodexBarWin.Services;

/// <summary>
/// Interface for WSL execution service.
/// </summary>
public interface IWslService
{
    /// <summary>
    /// Executes a command in WSL.
    /// </summary>
    Task<WslResult> ExecuteAsync(string command, CancellationToken ct = default);

    /// <summary>
    /// Executes independent commands together, notifying callers as each completes.
    /// The returned results have the same order as the commands.
    /// </summary>
    Task<IReadOnlyList<WslResult>> ExecuteBatchAsync(IReadOnlyList<WslBatchCommand> commands,
        Action<int, WslResult>? onCompleted = null, CancellationToken ct = default);

    /// <summary>
    /// Checks if WSL is installed.
    /// </summary>
    Task<bool> IsWslInstalledAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks WSL status with detailed error information.
    /// </summary>
    Task<WslStatusResult> CheckWslStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the list of installed WSL distributions.
    /// </summary>
    Task<List<string>> GetDistrosAsync(CancellationToken ct = default);
}

/// <summary>A shell command and its individual execution deadline.</summary>
public sealed record WslBatchCommand(string Command, TimeSpan Timeout);

/// <summary>
/// Result of a WSL command execution.
/// </summary>
public record WslResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public int ExitCode { get; init; }
}

/// <summary>
/// Result of a WSL status check.
/// </summary>
public record WslStatusResult
{
    public bool IsInstalled { get; init; }
    public bool IsRunning { get; init; }
    public WslErrorType? Error { get; init; }
    public string? ErrorMessage { get; init; }
}
