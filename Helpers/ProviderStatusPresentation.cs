using CodexBarWin.Models;

namespace CodexBarWin.Helpers;

public enum ProviderStatusSeverity { Neutral, Healthy, Warning, Error }

/// <summary>Pure status parsing and presentation shared by all provider displays.</summary>
public static class ProviderStatusPresentation
{
    public static ProviderStatusIndicator ParseIndicator(string? indicator) => indicator?.Trim().ToLowerInvariant() switch
    {
        "none" => ProviderStatusIndicator.None,
        "minor" => ProviderStatusIndicator.Minor,
        "major" => ProviderStatusIndicator.Major,
        "critical" => ProviderStatusIndicator.Critical,
        "maintenance" => ProviderStatusIndicator.Maintenance,
        _ => ProviderStatusIndicator.Unknown
    };

    public static ProviderStatusSeverity Severity(ProviderStatus? status) => status?.Indicator switch
    {
        ProviderStatusIndicator.None => ProviderStatusSeverity.Healthy,
        ProviderStatusIndicator.Minor or ProviderStatusIndicator.Maintenance => ProviderStatusSeverity.Warning,
        ProviderStatusIndicator.Major or ProviderStatusIndicator.Critical => ProviderStatusSeverity.Error,
        _ => ProviderStatusSeverity.Neutral
    };

    public static string ColorHex(ProviderStatus? status) => Severity(status) switch
    {
        ProviderStatusSeverity.Healthy => "#10B981",
        ProviderStatusSeverity.Warning => "#F59E0B",
        ProviderStatusSeverity.Error => "#EF4444",
        _ => "#9CA3AF"
    };

    public static bool IsIncident(ProviderStatus? status) =>
        Severity(status) is ProviderStatusSeverity.Warning or ProviderStatusSeverity.Error;

    public static bool ShowBanner(ProviderStatus? status) =>
        status != null && status.Indicator != ProviderStatusIndicator.None;

    public static string Description(ProviderStatus? status)
    {
        if (!string.IsNullOrWhiteSpace(status?.Description))
        {
            var sanitized = IncidentTextSanitizer.Sanitize(status.Description);
            if (!string.IsNullOrWhiteSpace(sanitized)) return sanitized;
        }

        return status?.Indicator switch
        {
            ProviderStatusIndicator.None => "All systems operational",
            ProviderStatusIndicator.Minor => "Minor service disruption",
            ProviderStatusIndicator.Major => "Major service disruption",
            ProviderStatusIndicator.Critical => "Critical service disruption",
            ProviderStatusIndicator.Maintenance => "Scheduled maintenance",
            _ => "No status available"
        };
    }

    public static Uri? StatusPageUri(ProviderStatus? status) =>
        Uri.TryCreate(status?.Url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri : null;
}
