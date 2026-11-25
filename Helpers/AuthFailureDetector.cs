using System.Text.RegularExpressions;
using CodexBarWin.Models;

namespace CodexBarWin.Helpers;

/// <summary>
/// Pure classification of final provider failures, after refresh/fallback routes have failed.
/// Keep app patterns here. Codex/Gemini wording is inferred until real logout fixtures are available.
/// Provider error code 3 alone means nothing about authentication. Infrastructure failures take precedence.
/// </summary>
public static class AuthFailureDetector
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex NonAuth = new(
        @"\b(429|5\d\d|127)\b|rate[ -]?limit|too many requests|timed?\s*out|timeout|network|connection|dns|service unavailable|outage|command not found",
        Options);
    // Add new observed message patterns here and matching positive/negative fixtures in the tests.
    private static readonly Regex Auth = new(string.Join("|", new[]
    {
        @"\b(401|403|unauthori[sz]ed)\b",
        @"\bnot (?:logged|signed) in\b",
        @"\b(?:log[ -]?in|sign[ -]?in|authentication|authorization) (?:is )?required\b",
        @"\b(?:please|must|need to) (?:log[ -]?in|sign[ -]?in|authenticate)\b",
        @"\b(?:log in|sign in)\b",
        @"\brun\s+[\x60'""]?(?:claude|codex|agy)\b[^\r\n]*(?:authenticate|login|log in|sign in)",
        @"\b(?:access[ _-]?token|refresh[ _-]?token|oauth[ _-]?token|token|credentials?|auth\.json)\b[^\r\n]{0,60}\b(?:missing|empty|not found|expired|revoked|invalid)\b",
        @"\b(?:missing|empty|expired|revoked|invalid)\s+(?:(?:oauth|access|refresh)\s+)?(?:tokens?|credentials?)\b",
        @"\binvalid_grant\b"
    }), Options);
    private static readonly Regex Secret = new(
        @"\bBearer\s+\S+|\b(?:access[ _-]?token|refresh[ _-]?token|api[ _-]?key|password|secret)[""']?\s*[=:]\s*[""']?[^\s,""';}]+[""']?|\b[A-Za-z0-9_+/=-]{32,}(?:\.[A-Za-z0-9_+/=-]+)*\b",
        Options);

    public static bool IsAuthFailure(string provider, int? errorCode, string? errorKind, string? message)
    {
        if (!ProviderConstants.IsValidProvider(provider)) return false;
        ProviderConstants.ValidateAndNormalize(provider);
        var text = message ?? string.Empty;
        if (errorCode is 127 or 124 or 125 or 429 or >= 500 and <= 599 ||
            NonAuth.IsMatch(text) || NonAuth.IsMatch(errorKind ?? string.Empty)) return false;
        return errorCode is 401 or 403 ||
            errorKind?.ToLowerInvariant() is "auth" or "authentication" or "unauthorized" or "auth_required" ||
            Auth.IsMatch(text);
    }

    /// <summary>Retain a bounded, single-line CLI explanation without credential values or opaque tokens.</summary>
    public static string? SanitizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var safe = Secret.Replace(message, "[redacted]");
        safe = Regex.Replace(safe, @"\s+", " ").Trim();
        return safe.Length <= 512 ? safe : safe[..512];
    }

    /// <summary>Plain-text sign-in hint shown next to the "Sign in" button; Gemini usage is provided by Antigravity.</summary>
    public static string GetSignInHint(string provider) => ProviderConstants.ValidateAndNormalize(provider) switch
    {
        "claude" => "Sign in to Claude Code to see your Claude limits.",
        "codex" => "Sign in to Codex to see your Codex limits.",
        "gemini" => "Sign in to Antigravity with Google to see your Gemini limits.",
        _ => string.Empty
    };
}
