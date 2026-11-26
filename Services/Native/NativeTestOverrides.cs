#if DEBUG
using System.Globalization;
using System.Text.RegularExpressions;
#endif

namespace AgentsWinBar.Services.Native;

/// <summary>
/// Test hooks for running a Debug build against synthetic sign-ins and the fake provider server
/// (tools/FakeProviders). A Debug build reads them once, at startup, from <c>AGENTSWINBAR_TEST_*</c> environment
/// variables. In a Release build <see cref="Current"/> and <see cref="Load"/> are always null, nothing is read, and the
/// HTTP allow list has no loopback exception, so a Release app cannot be pointed anywhere else.
/// </summary>
public sealed record NativeTestOverrides
{
    /// <summary>
    /// Stands in for %USERPROFILE%: the sign-in files (.claude\.credentials.json, .codex\auth.json), the cost log
    /// folders and the Gemini spend log are read under it.
    /// </summary>
    public string? ProfileRoot { get; init; }

    /// <summary>Stands in for %LOCALAPPDATA%\AgentsWinBar: settings, caches, the cost index, the cooldown file and crash.log.</summary>
    public string? AppDataDirectory { get; init; }

    /// <summary>A file holding a fake Antigravity credential blob, read instead of Windows Credential Manager.</summary>
    public string? AntigravityCredentialFile { get; init; }

    /// <summary>An http://127.0.0.1:&lt;port&gt;/ origin that replaces Claude's usage host (the path stays the same).</summary>
    public Uri? ClaudeOrigin { get; init; }

    /// <summary>An http://127.0.0.1:&lt;port&gt;/ origin that replaces Codex's usage host.</summary>
    public Uri? CodexOrigin { get; init; }

    /// <summary>An http://127.0.0.1:&lt;port&gt;/ origin that replaces both Antigravity (Gemini) hosts.</summary>
    public Uri? AntigravityOrigin { get; init; }

    /// <summary>The origins the HTTP allow list accepts besides the real hosts (Debug builds only).</summary>
    public IReadOnlyList<Uri> LoopbackOrigins =>
        new[] { ClaudeOrigin, CodexOrigin, AntigravityOrigin }.OfType<Uri>().Distinct().ToArray();

#if DEBUG
    private static readonly Lazy<NativeTestOverrides?> CurrentValue = new(() => Load(Environment.GetEnvironmentVariable));

    /// <summary>This process's overrides, read on first use; null when no test variable is set.</summary>
    /// <exception cref="InvalidOperationException">A test variable is set but invalid: the app refuses to start.</exception>
    public static NativeTestOverrides? Current => CurrentValue.Value;
#else
    /// <summary>Always null: a Release build has no test hooks.</summary>
    public static NativeTestOverrides? Current => null;
#endif

    /// <summary>
    /// Reads the overrides through <paramref name="read"/> (an environment lookup). A Release build returns null
    /// without calling it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Debug only: a variable is set but invalid. Failing is safer than falling back to the real profile or hosts.
    /// </exception>
    internal static NativeTestOverrides? Load(Func<string, string?> read)
    {
#if DEBUG
        return Parse(read);
#else
        return null;
#endif
    }

    /// <summary>The production URI with its scheme, host and port replaced by <paramref name="origin"/> (when set).</summary>
    internal static Uri Rebase(Uri production, Uri? origin) => origin is null ? production : new Uri(origin, production.PathAndQuery);

    /// <summary>Claude's reader options with the sign-in file under <see cref="ProfileRoot"/> and the usage URI rebased.</summary>
    internal ClaudeNativeOptions Apply(ClaudeNativeOptions options) => options with
    {
        CredentialsPath = ProfileRoot is { } root ? Path.Combine(root, ".claude", ".credentials.json") : options.CredentialsPath,
        UsageUri = Rebase(options.UsageUri, ClaudeOrigin)
    };

    /// <summary>Codex's reader options with the sign-in file under <see cref="ProfileRoot"/> and the usage URI rebased.</summary>
    internal CodexNativeOptions Apply(CodexNativeOptions options) => options with
    {
        AuthPath = ProfileRoot is { } root ? Path.Combine(root, ".codex", "auth.json") : options.AuthPath,
        UsageUri = Rebase(options.UsageUri, CodexOrigin)
    };

    /// <summary>Antigravity's reader options with all three endpoints rebased onto one origin.</summary>
    internal AntigravityNativeOptions Apply(AntigravityNativeOptions options) => options with
    {
        LoadCodeAssistUri = Rebase(options.LoadCodeAssistUri, AntigravityOrigin),
        QuotaSummaryUri = Rebase(options.QuotaSummaryUri, AntigravityOrigin),
        AvailableModelsUri = Rebase(options.AvailableModelsUri, AntigravityOrigin)
    };

    /// <summary>Where Claude's 429 cooldown is kept, or null for the default place.</summary>
    internal string? CooldownFilePath =>
        AppDataDirectory is { } directory ? Path.Combine(directory, ClaudeCooldownFile.FileName) : null;

    /// <summary>The HTTP client for the native readers; a Debug build also lets it reach <see cref="LoopbackOrigins"/>.</summary>
    internal HttpClient CreateHttpClient()
    {
#if DEBUG
        return NativeHttp.CreateClient(LoopbackOrigins);
#else
        return NativeHttp.CreateClient();
#endif
    }

    /// <summary>The fake Antigravity credential source (Debug builds only), or null for Credential Manager.</summary>
    internal IAntigravityCredentialSource? CreateAntigravitySource()
    {
#if DEBUG
        return AntigravityCredentialFile is { } file ? new FileAntigravityCredentialSource(file) : null;
#else
        return null;
#endif
    }

#if DEBUG
    internal const string Prefix = "AGENTSWINBAR_TEST_";
    internal const string ProfileRootVariable = Prefix + "PROFILE_ROOT";
    internal const string AppDataVariable = Prefix + "APPDATA";
    internal const string AntigravityCredentialFileVariable = Prefix + "ANTIGRAVITY_CREDENTIAL_FILE";
    internal const string ClaudeBaseUrlVariable = Prefix + "CLAUDE_BASE_URL";
    internal const string CodexBaseUrlVariable = Prefix + "CODEX_BASE_URL";
    internal const string AntigravityBaseUrlVariable = Prefix + "ANTIGRAVITY_BASE_URL";

    private static readonly Regex LoopbackOrigin = new(@"^http://127\.0\.0\.1:(?<port>[0-9]{1,5})/?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    private static NativeTestOverrides? Parse(Func<string, string?> read)
    {
        var overrides = new NativeTestOverrides
        {
            ProfileRoot = FullPath(read, ProfileRootVariable),
            AppDataDirectory = FullPath(read, AppDataVariable),
            AntigravityCredentialFile = FullPath(read, AntigravityCredentialFileVariable),
            ClaudeOrigin = Origin(read, ClaudeBaseUrlVariable),
            CodexOrigin = Origin(read, CodexBaseUrlVariable),
            AntigravityOrigin = Origin(read, AntigravityBaseUrlVariable)
        };
        if (overrides == new NativeTestOverrides()) return null;

        // The fake server must only ever see fake sign-ins: a provider is pointed at it only together with its
        // credential override, so a real token can never travel over plain http.
        if ((overrides.ClaudeOrigin ?? overrides.CodexOrigin) is not null && overrides.ProfileRoot is null)
            throw new InvalidOperationException(
                $"{ClaudeBaseUrlVariable} and {CodexBaseUrlVariable} need {ProfileRootVariable} (a fake profile).");
        if (overrides.AntigravityOrigin is not null && overrides.AntigravityCredentialFile is null)
            throw new InvalidOperationException(
                $"{AntigravityBaseUrlVariable} needs {AntigravityCredentialFileVariable} (a fake credential).");
        // Fake sign-ins and readings must not land in the real settings, caches and cost index.
        if (overrides.AppDataDirectory is null)
            throw new InvalidOperationException($"Every {Prefix}* override needs {AppDataVariable} (a separate data folder).");
        return overrides;
    }

    private static string? Value(Func<string, string?> read, string name) =>
        read(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string? FullPath(Func<string, string?> read, string name)
    {
        if (Value(read, name) is not { } value) return null;
        if (!Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"{name} must be an absolute path.");
        return Path.GetFullPath(value);
    }

    private static Uri? Origin(Func<string, string?> read, string name)
    {
        if (Value(read, name) is not { } value) return null;
        var match = LoopbackOrigin.Match(value);
        if (!match.Success || !int.TryParse(match.Groups["port"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture,
                out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException($"{name} must be http://127.0.0.1:<port> (port 1-65535), nothing else.");
        return new Uri($"http://127.0.0.1:{port}/");
    }
#endif
}

#if DEBUG
/// <summary>
/// Debug test hook: reads a fake Antigravity credential blob from a file instead of Windows Credential Manager.
/// A missing or empty file is no sign-in.
/// </summary>
internal sealed class FileAntigravityCredentialSource(string path) : IAntigravityCredentialSource
{
    public string FilePath { get; } = path;

    public SensitiveBuffer? ReadBlob()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length == 0) return null;
        if (info.Length > NativeCredentialFile.MaxBytes) throw new IOException("Credential file is unexpectedly large.");
        var bytes = File.ReadAllBytes(FilePath);
        try
        {
            return bytes.Length == 0 ? null : SensitiveBuffer.From(bytes);
        }
        finally { Array.Clear(bytes); }
    }
}
#endif
