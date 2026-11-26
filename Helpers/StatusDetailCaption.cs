using AgentsWinBar.Models;

namespace AgentsWinBar.Helpers;

/// <summary>
/// Pure display strings for the incident detail, the status-dot tooltips, the "Last fetched" line and the
/// "showing last-known data" note. Thin x:Bind wrappers live in DashboardFormat.
/// </summary>
public static class StatusDetailCaption
{
    /// <summary>Banner title line: the incident title, or empty when there is none or it just repeats the description.</summary>
    public static string BannerTitle(string? incidentTitle, string? description)
    {
        var title = incidentTitle?.Trim() ?? string.Empty;
        return title.Length == 0 || string.Equals(title, description?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? string.Empty : title;
    }

    /// <summary>Banner title line for a dashboard provider; empty when there is no incident detail.</summary>
    public static string BannerTitle(DashboardProvider? provider) => provider == null ? string.Empty
        : BannerTitle(IncidentCaption.Title(provider, string.Empty), ProviderStatusPresentation.Description(provider.ServiceStatus));

    /// <summary>Banner title line for a status (detail page); empty when healthy, absent or the same as the description.</summary>
    public static string BannerTitle(ProviderStatus? status) =>
        BannerTitle(IncidentCaption.Title(status, string.Empty), ProviderStatusPresentation.Description(status));

    /// <summary>Tooltip for a status dot: description, then incident title, update text and update age (each only when present).</summary>
    public static string Tooltip(string? description, string? incidentTitle, string? updateBody, DateTime? updateAtUtc, DateTime nowUtc)
    {
        var lines = new List<string>(4);
        var desc = description?.Trim() ?? string.Empty;
        if (desc.Length > 0) lines.Add(desc);
        var title = BannerTitle(incidentTitle, desc);
        if (title.Length > 0) lines.Add(title);
        var body = updateBody?.Trim() ?? string.Empty;
        if (body.Length > 0) lines.Add(body);
        var age = IncidentCaption.AgeText(updateAtUtc, nowUtc);
        if (age.Length > 0) lines.Add(age);
        return string.Join('\n', lines);
    }

    /// <summary>Status dot tooltip for a status; just the description when healthy or no incident detail is known.</summary>
    public static string Tooltip(ProviderStatus? status, DateTime nowUtc) =>
        status == null || status.Indicator == ProviderStatusIndicator.None
            ? ProviderStatusPresentation.Description(status)
            : Tooltip(ProviderStatusPresentation.Description(status), IncidentCaption.Title(status, string.Empty),
                IncidentCaption.UpdateBody(status), status.LatestUpdateAt, nowUtc);

    /// <summary>Status dot tooltip for a dashboard provider.</summary>
    public static string Tooltip(DashboardProvider? provider, DateTime nowUtc)
    {
        var status = provider?.ServiceStatus;
        if (provider == null || status == null || status.Indicator == ProviderStatusIndicator.None)
            return ProviderStatusPresentation.Description(status);
        return Tooltip(ProviderStatusPresentation.Description(status), IncidentCaption.Title(provider, string.Empty),
            IncidentCaption.UpdateBody(provider), provider.IncidentUpdateAtUtc ?? status.LatestUpdateAt, nowUtc);
    }

    /// <summary>
    /// The detail page's "Last fetched" text when usage is last-known-good:
    /// "Fetched Sun 11 Oct, 3:34 AM · 9m ago · Stale, last good reading 2h ago". The reading's age is left out when
    /// it equals the fetch age, so it is never said twice. Empty when fresh (the caller keeps its normal text).
    /// </summary>
    public static string FetchedWhenStale(string fetchedLocal, DateTime fetchedUtc, bool usageIsStale, DateTime? lastSuccessUtc, DateTime nowUtc)
    {
        if (!usageIsStale) return string.Empty;
        var fetchedAgo = StaleCaption.Age(fetchedUtc, nowUtc);
        var text = $"Fetched {fetchedLocal} · {fetchedAgo} · Stale, ";
        if (lastSuccessUtc is not { } at) return text + "last good reading time unknown";
        return StaleCaption.Age(at, nowUtc) == fetchedAgo ? text + "showing last good reading" : text + $"last good reading {StaleCaption.Age(at, nowUtc)}";
    }

    /// <summary>"Showing last-known data from 9m ago" when stale usage is displayed; empty otherwise.</summary>
    public static string LastKnownData(bool usageIsStale, bool hasUsageShown, DateTime? lastSuccessUtc, DateTime nowUtc) =>
        !usageIsStale || !hasUsageShown ? string.Empty
        : lastSuccessUtc is { } at ? $"Showing last-known data from {StaleCaption.Age(at, nowUtc)}"
        : "Showing last-known data";

    /// <summary>Last-known-data note for a dashboard provider's failed-fetch banner.</summary>
    public static string LastKnownData(DashboardProvider? provider, DateTime nowUtc) => provider == null ? string.Empty
        : LastKnownData(provider.UsageIsStale, provider.Windows.Count > 0, provider.LastSuccessfulUsageFetchUtc, nowUtc);
}
