using CodexBarWin.Models;

namespace CodexBarWin.Helpers;

/// <summary>Shell-safe Windows log paths, used only by cost scans.</summary>
public static class CostCommandBuilder
{
    public static string ToWslPath(string windowsPath)
    {
        if (string.IsNullOrWhiteSpace(windowsPath) || windowsPath.Any(c => c is '\'' or '"' || char.IsControl(c)))
            throw new ArgumentException("Log paths cannot contain quotes or control characters.", nameof(windowsPath));
        if (windowsPath.Length < 3 || !char.IsAsciiLetter(windowsPath[0]) || windowsPath[1] != ':' || windowsPath[2] is not ('\\' or '/'))
            throw new ArgumentException("A local absolute Windows drive path is required.", nameof(windowsPath));
        return $"/mnt/{char.ToLowerInvariant(windowsPath[0])}/{windowsPath[3..].Replace('\\', '/')}";
    }

    public static string Build(string provider, string userProfile, Func<string, bool> directoryExists)
    {
        var normalized = ProviderConstants.ValidateAndNormalize(provider);
        var cliProvider = normalized == "gemini" ? "antigravity" : normalized;
        var environment = string.Empty;
        if (normalized is "claude" or "codex")
        {
            var folder = userProfile.TrimEnd('\\', '/') + (normalized == "claude" ? "\\.claude" : "\\.codex");
            // Validate even if the folder is missing; never forward unsafe input to a shell.
            var wslFolder = ToWslPath(folder);
            if (directoryExists(folder))
                environment = $"env {(normalized == "claude" ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME")}='{wslFolder}' ";
        }
        return $"{environment}codexbar cost --provider {cliProvider} --format json --refresh";
    }
}
