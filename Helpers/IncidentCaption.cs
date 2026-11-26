using AgentsWinBar.Models;

namespace AgentsWinBar.Helpers;

/// <summary>
/// Pure helper returning display strings for provider incidents: title line, sanitized update body, and relative update age text.
/// Reuses <see cref="StaleCaption.Age(DateTime, DateTime)"/> for consistent age formatting.
/// </summary>
public static class IncidentCaption
{
    public const string DefaultFallbackTitle = "Provider service status";

    /// <summary>
    /// Returns the incident title if present, or <paramref name="fallback"/> (defaults to "Provider service status").
    /// If the status indicator is <see cref="ProviderStatusIndicator.None"/>, always returns <paramref name="fallback"/>.
    /// </summary>
    public static string Title(ProviderStatus? status, string fallback = DefaultFallbackTitle)
    {
        if (status == null || status.Indicator == ProviderStatusIndicator.None)
        {
            return fallback;
        }

        return Title(status.IncidentTitle, fallback);
    }

    /// <summary>
    /// Returns the incident title if non-empty, or <paramref name="fallback"/>.
    /// </summary>
    public static string Title(string? incidentTitle, string fallback = DefaultFallbackTitle)
    {
        var sanitized = IncidentTextSanitizer.Sanitize(incidentTitle);
        return !string.IsNullOrWhiteSpace(sanitized) ? sanitized : fallback;
    }

    /// <summary>
    /// Returns the incident title from <paramref name="provider"/>, or <paramref name="fallback"/>.
    /// </summary>
    public static string Title(DashboardProvider? provider, string fallback = DefaultFallbackTitle) =>
        Title(provider?.IncidentTitle ?? provider?.ServiceStatus?.IncidentTitle, fallback);

    /// <summary>
    /// Returns the sanitized update body, capped to <paramref name="maxLength"/> characters (defaults to 200).
    /// If status is null or healthy (<see cref="ProviderStatusIndicator.None"/>), returns <see cref="string.Empty"/>.
    /// </summary>
    public static string UpdateBody(ProviderStatus? status, int maxLength = IncidentTextSanitizer.DefaultMaxLength)
    {
        if (status == null || status.Indicator == ProviderStatusIndicator.None)
        {
            return string.Empty;
        }

        return UpdateBody(status.LatestUpdateText, maxLength);
    }

    /// <summary>
    /// Returns the sanitized update body, capped to <paramref name="maxLength"/> characters (defaults to 200).
    /// </summary>
    public static string UpdateBody(string? text, int maxLength = IncidentTextSanitizer.DefaultMaxLength) =>
        IncidentTextSanitizer.Sanitize(text, maxLength);

    /// <summary>
    /// Returns the update body from <paramref name="provider"/>, capped to <paramref name="maxLength"/> characters.
    /// </summary>
    public static string UpdateBody(DashboardProvider? provider, int maxLength = IncidentTextSanitizer.DefaultMaxLength) =>
        UpdateBody(provider?.IncidentUpdateText ?? provider?.ServiceStatus?.LatestUpdateText, maxLength);

    /// <summary>
    /// Returns relative age text like "Update 3h ago", "Update just now", or empty string when <paramref name="updateAtUtc"/> is null.
    /// </summary>
    public static string AgeText(DateTime? updateAtUtc, DateTime nowUtc) =>
        updateAtUtc is { } at ? $"Update {StaleCaption.Age(at, nowUtc)}" : string.Empty;

    /// <summary>
    /// Returns relative age text like "Update 3h ago", evaluated against <see cref="DateTime.UtcNow"/>.
    /// </summary>
    public static string AgeText(DateTime? updateAtUtc) =>
        AgeText(updateAtUtc, DateTime.UtcNow);

    /// <summary>
    /// Returns relative age text for <paramref name="status"/>'s <see cref="ProviderStatus.LatestUpdateAt"/>.
    /// If status is null or healthy (<see cref="ProviderStatusIndicator.None"/>), returns <see cref="string.Empty"/>.
    /// </summary>
    public static string AgeText(ProviderStatus? status, DateTime nowUtc)
    {
        if (status == null || status.Indicator == ProviderStatusIndicator.None)
        {
            return string.Empty;
        }

        return AgeText(status.LatestUpdateAt, nowUtc);
    }

    /// <summary>
    /// Returns relative age text for <paramref name="status"/> evaluated against <see cref="DateTime.UtcNow"/>.
    /// </summary>
    public static string AgeText(ProviderStatus? status) =>
        AgeText(status, DateTime.UtcNow);

    /// <summary>
    /// Returns relative age text for <paramref name="provider"/>'s incident update.
    /// </summary>
    public static string AgeText(DashboardProvider? provider, DateTime nowUtc) =>
        AgeText(provider?.IncidentUpdateAtUtc ?? provider?.ServiceStatus?.LatestUpdateAt, nowUtc);

    /// <summary>
    /// Returns relative age text for <paramref name="provider"/> evaluated against <see cref="DateTime.UtcNow"/>.
    /// </summary>
    public static string AgeText(DashboardProvider? provider) =>
        AgeText(provider, DateTime.UtcNow);
}
