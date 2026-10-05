using System.Text.RegularExpressions;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Service for checking application setup status.
/// </summary>
public partial class SetupChecker : ISetupChecker
{
    private readonly IWslService _wslService;
    private readonly ILogger<SetupChecker> _logger;
    private readonly ISetupMemoService? _memo;

    public static readonly Version MinCodexBarVersion = new("0.17.0");

    public SetupChecker(
        IWslService wslService,
        ILogger<SetupChecker> logger,
        ISetupMemoService? memo = null)
    {
        _wslService = wslService;
        _logger = logger;
        _memo = memo;
    }

    public async Task<SetupStatus> CheckAsync(CancellationToken ct = default)
    {
        var status = await CheckCoreAsync(ct);
        if (_memo != null)
        {
            if (status.IsReady) await _memo.SaveAsync(status.CodexBarVersion, ct);
            else await _memo.InvalidateAsync(ct);
        }
        return status;
    }

    private async Task<SetupStatus> CheckCoreAsync(CancellationToken ct)
    {
        _logger.LogInformation("Checking setup status...");

        // 1. Check WSL status with details
        var wslStatus = await _wslService.CheckWslStatusAsync(ct);
        if (!wslStatus.IsInstalled)
        {
            _logger.LogWarning("WSL is not installed");
            return new SetupStatus
            {
                WslInstalled = false,
                WslError = wslStatus.Error ?? WslErrorType.NotInstalled
            };
        }

        if (!wslStatus.IsRunning)
        {
            _logger.LogWarning("WSL is installed but not running: {Error}", wslStatus.ErrorMessage);
            return new SetupStatus
            {
                WslInstalled = true,
                WslRunning = false,
                WslError = wslStatus.Error
            };
        }

        // 2. Check WSL distros
        var distros = await _wslService.GetDistrosAsync(ct);
        if (distros.Count == 0)
        {
            _logger.LogWarning("No WSL distros found");
            return new SetupStatus
            {
                WslInstalled = true,
                WslRunning = true,
                Distros = []
            };
        }

        _logger.LogInformation("Found WSL distros: {Distros}", string.Join(", ", distros));

        // Detect installation and retrieve the version with one interactive shell.
        WslResult versionResult;
        try
        {
            versionResult = await _wslService.ExecuteAsync("codexbar --version", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to check codexbar installation");
            versionResult = new WslResult { ExitCode = 127 };
        }

        if (versionResult.ExitCode == 127
            || versionResult.Output.Contains("command not found", StringComparison.OrdinalIgnoreCase)
            || versionResult.Error.Contains("command not found", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("codexbar is not installed in WSL");
            return new SetupStatus
            {
                WslInstalled = true,
                WslRunning = true,
                Distros = distros,
                CodexBarInstalled = false
            };
        }

        var version = versionResult.Success ? versionResult.Output.Trim() : null;
        _logger.LogInformation("codexbar version: {Version}", version);

        var isCompatible = IsCompatibleVersion(version);
        if (!isCompatible)
        {
            _logger.LogWarning("codexbar version {Version} is not compatible (minimum: {MinVersion})",
                version, MinCodexBarVersion);
        }

        return new SetupStatus
        {
            WslInstalled = true,
            WslRunning = true,
            Distros = distros,
            CodexBarInstalled = true,
            CodexBarVersion = version,
            IsReady = isCompatible
        };
    }

    private static bool IsCompatibleVersion(string? versionString)
    {
        if (string.IsNullOrWhiteSpace(versionString))
        {
            return false;
        }

        // If version contains "unknown", assume it's compatible (development build)
        if (versionString.Contains("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var match = VersionRegex().Match(versionString);
        if (!match.Success)
        {
            // If we can't parse version but codexbar is available, assume compatible
            return true;
        }

        return Version.TryParse(match.Groups[1].Value, out var version)
               && version >= MinCodexBarVersion;
    }

    [GeneratedRegex(@"(\d+\.\d+\.\d+)")]
    private static partial Regex VersionRegex();
}
