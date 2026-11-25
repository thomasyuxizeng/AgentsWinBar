using CodexBarWin.Models;

namespace CodexBarWin.Helpers;

/// <summary>A provider's sign-in as the app last saw it.</summary>
public enum SignInState
{
    /// <summary>Not read yet in this session.</summary>
    Unknown,
    SignedIn,
    /// <summary>Missing or rejected: the user has to sign in (the only state that offers a "Sign in" button).</summary>
    SignedOut,
    /// <summary>Expired: the provider's own tool renews it, so the app waits instead of asking for a sign-in.</summary>
    Expired
}

/// <summary>Sign-in state and wording for the Settings "Sign-ins" section.</summary>
public static class SignInStatus
{
    public static SignInState Of(UsageData? data) => data switch
    {
        null => SignInState.Unknown,
        { AuthRequired: true } => SignInState.SignedOut,
        { SignInExpired: true } => SignInState.Expired,
        _ when data.HasUsage || data.UsageFetchOutcome == FetchOutcome.Success => SignInState.SignedIn,
        _ => SignInState.Unknown
    };

    /// <summary>Only a missing or rejected sign-in offers the button; an expired one is renewed by the provider's tool.</summary>
    public static bool CanSignIn(SignInState state) => state == SignInState.SignedOut;

    /// <summary>The tool that owns the provider's sign-in: "Claude Code", "Codex", "Antigravity".</summary>
    public static string ToolName(string provider) => ProviderConstants.ValidateAndNormalize(provider) switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        "gemini" => "Antigravity",
        _ => string.Empty
    };

    public static string Text(string provider, SignInState state) => state switch
    {
        SignInState.SignedIn => "Signed in",
        SignInState.SignedOut => "Signed out",
        SignInState.Expired => $"Expired, waiting for {ToolName(provider)} to renew it",
        _ => "Not checked yet"
    };
}
