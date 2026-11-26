using AgentsWinBar.Models;
using AgentsWinBar.Services;

namespace AgentsWinBar.Tests.Unit.Mocks;

/// <summary>
/// Mock implementation of IWslService for testing.
/// </summary>
public class MockWslService : IWslService
{
    private readonly Dictionary<string, WslResult> _commandResults = new();
    private WslStatusResult _statusResult = new() { IsInstalled = true, IsRunning = true };
    private List<string> _distros = ["Ubuntu"];

    public List<string> ExecutedCommands { get; } = [];
    public List<TimeSpan> ExecutedCommandTimeouts { get; } = [];
    public List<IReadOnlyList<WslBatchCommand>> ExecutedBatches { get; } = [];
    public Func<IReadOnlyList<WslBatchCommand>, Action<int, WslResult>?, CancellationToken,
        Task<IReadOnlyList<WslResult>>>? BatchHandler { get; set; }
    public Func<string, CancellationToken, Task<WslResult>>? CommandHandler { get; set; }

    public async Task<IReadOnlyList<WslResult>> ExecuteBatchAsync(IReadOnlyList<WslBatchCommand> commands,
        Action<int, WslResult>? onCompleted = null, CancellationToken ct = default)
    {
        ExecutedBatches.Add(commands);
        if (BatchHandler != null) return await BatchHandler(commands, onCompleted, ct);
        var results = new List<WslResult>();
        for (var i = 0; i < commands.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ExecuteAsync(commands[i].Command, ct);
            results.Add(result);
            onCompleted?.Invoke(i, result);
        }
        return results;
    }

    public void SetCommandResult(string command, WslResult result)
    {
        _commandResults[command] = result;
    }

    public void SetStatusResult(WslStatusResult result)
    {
        _statusResult = result;
    }

    public void SetDistros(List<string> distros)
    {
        _distros = distros;
    }

    public Task<WslResult> ExecuteAsync(string command, CancellationToken ct = default)
    {
        ExecutedCommands.Add(command);
        if (CommandHandler != null) return CommandHandler(command, ct);

        if (_commandResults.TryGetValue(command, out var result))
        {
            return Task.FromResult(result);
        }

        // Default success result
        return Task.FromResult(new WslResult
        {
            Success = true,
            Output = "[]",
            ExitCode = 0
        });
    }

    public Task<WslResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        ExecutedCommandTimeouts.Add(timeout);
        return ExecuteAsync(command, ct);
    }

    public Task<bool> IsWslInstalledAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_statusResult.IsInstalled);
    }

    public Task<WslStatusResult> CheckWslStatusAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_statusResult);
    }

    public Task<List<string>> GetDistrosAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_distros);
    }
}
