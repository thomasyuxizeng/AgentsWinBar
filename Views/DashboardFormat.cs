using System.Globalization;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CodexBarWin.Views;

/// <summary>
/// Presentation helpers for the dashboard: pure formatting of snapshot values for x:Bind function bindings.
/// Nothing here computes data (the snapshot already did); it only turns values into text, colours and visibility.
/// </summary>
internal static class DashboardFormat
{
    private static readonly SolidColorBrush Amber = new(Color.FromArgb(255, 255, 185, 0));
    private static readonly SolidColorBrush Red = new(Color.FromArgb(255, 232, 17, 35));
    private static readonly SolidColorBrush Green = new(Color.FromArgb(255, 16, 163, 127));
    private static readonly SolidColorBrush Grey = new(Color.FromArgb(255, 156, 163, 175));
    private static readonly SolidColorBrush White = new(Colors.White);
    private static readonly SolidColorBrush NearBlack = new(Color.FromArgb(0xDD, 0, 0, 0));

    public static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisNot(bool hidden) => hidden ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisText(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisNotNull(object? value) => value == null ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisCount(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisEmpty(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static string Percent(double value) => $"{FormatPercent(value):F0}%";
    public static string Used(double value) => $"{FormatPercent(value):F0}% used";
    private static double FormatPercent(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
    public static double Time(double? elapsedPercent) => elapsedPercent is { } value && double.IsFinite(value) ? DashboardValues.Percent(value) : -1;

    /// <summary>"Near limit" / "At limit" chip (75% / 90%, UsageThresholds, the same as the bars); hidden below 75%.</summary>
    public static string LimitLabel(double percent) => percent >= UsageThresholds.Critical ? "At limit" : percent >= UsageThresholds.Warning ? "Near limit" : string.Empty;
    public static Visibility LimitVis(double percent) => Vis(percent >= UsageThresholds.Warning);
    /// <summary>Chip text colour: white on the red chip, near-black on the amber one (both readable in either theme).</summary>
    public static Brush LimitForeground(double percent) => percent >= UsageThresholds.Critical ? White : NearBlack;
    /// <summary>"RESET EARLY" chip and its detail line for a window reset ahead of schedule (empty / collapsed otherwise).</summary>
    public static string EarlyChip(DashboardWindow? window) => EarlyResetDisplay.Chip(window?.EarlyResetAtUtc);
    public static Visibility EarlyVis(DashboardWindow? window) => Vis(window?.EarlyResetAtUtc != null);
    public static string EarlyDetail(DashboardWindow? window) => window == null ? string.Empty
        : EarlyResetDisplay.Detail(window.EarlyResetAtUtc, window.EarlyResetFromPercent, window.EarlyResetToPercent, DateTime.UtcNow);
    public static Visibility VisEarlyDetail(DashboardWindow? window) => VisText(EarlyDetail(window));
    public static Brush EarlyBrush() => Green;

    public static Brush LimitBrush(double percent) => percent >= UsageThresholds.Critical ? Red : Amber;

    public static string Money(decimal value, string currency) => CostFormatter.Money(value, currency);
    public static string Tokens(long tokens) => CostFormatter.Tokens(tokens);
    public static string TokenCount(long tokens) => $"{CostFormatter.Tokens(tokens)} tokens";
    public static string MoneyOrDash(decimal? value, string currency) => value.HasValue ? CostFormatter.Money(value.Value, currency) : "—";

    /// <summary>"Sat 4 Oct, 9:00 PM" in the user's timezone, or an em dash when unknown.</summary>
    public static string Local(DateTime? utc)
    {
        if (utc is not { } value) return "—";
        var local = TimeZoneInfo.ConvertTimeFromUtc(DashboardValues.Utc(value), TimeZoneInfo.Local);
        return local.ToString("ddd d MMM, h:mm tt", CultureInfo.CurrentCulture);
    }

    public static string LocalTime(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DashboardValues.Utc(utc), TimeZoneInfo.Local)
            .ToString("h:mm:ss tt", CultureInfo.CurrentCulture);

    public static string LocalDate(DateTime? utc)
    {
        if (utc is not { } value) return "—";
        return TimeZoneInfo.ConvertTimeFromUtc(DashboardValues.Utc(value), TimeZoneInfo.Local)
            .ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>"2m ago", "3h ago" relative to now.</summary>
    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - DashboardValues.Utc(utc);
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalSeconds < 45 ? "just now"
            : span.TotalMinutes < 60 ? $"{(int)Math.Round(span.TotalMinutes)}m ago"
            : span.TotalHours < 48 ? $"{(int)span.TotalHours}h {span.Minutes}m ago"
            : $"{(int)span.TotalDays}d ago";
    }

    public static string In(DateTime? utc)
    {
        if (utc is not { } value) return "—";
        var span = DashboardValues.Utc(value) - DateTime.UtcNow;
        if (span <= TimeSpan.Zero) return "now";
        return span.TotalMinutes < 1 ? "<1m" : "in " + PaceCalculator.FormatDuration(span.TotalMinutes);
    }

    public static string WindowLength(int minutes) => minutes <= 0 ? "unknown length"
        : minutes % 1440 == 0 ? $"{minutes / 1440}-day window"
        : minutes % 60 == 0 ? $"{minutes / 60}-hour window" : $"{minutes}-minute window";

    /// <summary>"5-hour window · 62% of the time has passed".</summary>
    public static string WindowMeta(int windowMinutes, double? elapsedPercent) => elapsedPercent is double elapsed && double.IsFinite(elapsed)
        ? $"{WindowLength(windowMinutes)} · {DashboardValues.Percent(elapsed):F0}% of the time has passed"
        : WindowLength(windowMinutes);

    public static string ResetLine(string? resetText, DateTime? resetsAtUtc) =>
        string.IsNullOrEmpty(resetText) ? Local(resetsAtUtc)
        : resetsAtUtc.HasValue ? $"{resetText} · {Local(resetsAtUtc)}" : resetText;

    // Provider service status
    public static Brush StatusBrush(ProviderStatus? status) => Controls.ProviderStatusVisuals.Brush(status);
    public static string StatusText(ProviderStatus? status) => ProviderStatusPresentation.Description(status);
    public static string StatusChecked(ProviderStatus? status) => status?.UpdatedAt is { } at
        ? $"Status page updated {Ago(at)}" : status == null ? "No status reported" : string.Empty;
    /// <summary>The "status page updated" line, replaced by the stale caption when the status is last-known-good.</summary>
    public static string StatusChecked(DashboardProvider? provider) => provider == null ? string.Empty
        : StaleCaption.StatusLine(provider.StatusIsStale, provider.LastSuccessfulStatusFetchUtc, DateTime.UtcNow,
            StatusChecked(provider.ServiceStatus));
    // Incident detail (banner lines and status dot tooltips); all empty/collapsed when the provider has no incident detail.
    public static string IncidentTitle(DashboardProvider? provider) => StatusDetailCaption.BannerTitle(provider);
    public static Visibility VisIncidentTitle(DashboardProvider? provider) => VisText(IncidentTitle(provider));
    public static string IncidentBody(DashboardProvider? provider) =>
        provider?.ServiceStatus is { Indicator: not ProviderStatusIndicator.None } ? IncidentCaption.UpdateBody(provider) : string.Empty;
    public static Visibility VisIncidentBody(DashboardProvider? provider) => VisText(IncidentBody(provider));
    public static string IncidentAge(DashboardProvider? provider) =>
        provider?.ServiceStatus is { Indicator: not ProviderStatusIndicator.None } ? IncidentCaption.AgeText(provider, DateTime.UtcNow) : string.Empty;
    public static Visibility VisIncidentAge(DashboardProvider? provider) => VisText(IncidentAge(provider));
    /// <summary>Status dot tooltip: description, incident title, update text and update age.</summary>
    public static string StatusTip(DashboardProvider? provider) => StatusDetailCaption.Tooltip(provider, DateTime.UtcNow);
    /// <summary>Line under a failed-fetch message when the usage shown is last-known-good; empty otherwise.</summary>
    public static string LastKnownData(DashboardProvider? provider) => StatusDetailCaption.LastKnownData(provider, DateTime.UtcNow);
    public static Visibility VisLastKnownData(DashboardProvider? provider) => VisText(LastKnownData(provider));
    public static Uri? StatusUri(ProviderStatus? status) => ProviderStatusPresentation.StatusPageUri(status);
    public static Visibility StatusLink(ProviderStatus? status) => VisNotNull(ProviderStatusPresentation.StatusPageUri(status));

    public static Geometry Icon(string? providerId) => ProviderIcons.GetGeometry(providerId ?? string.Empty);
    public static string ProviderName(string providerId) => ProviderIcons.GetDisplayName(providerId);
    public static string RefreshTitle(int count) => count == 0 ? "REFRESH LOG" : $"REFRESH LOG · LAST {count}";

    /// <summary>
    /// The provider header's one freshness line, with the source as a quiet suffix:
    /// "Updated 2m ago · via oauth", "Stale · last updated 3h ago · via cli", "Reading is 263d old · fetched just now · via api".
    /// </summary>
    public static string FreshnessLine(DashboardProvider? provider)
    {
        if (provider == null) return string.Empty;
        var text = provider.IsLoading ? "Refreshing…"
            : provider.UsageIsStale ? StaleCaption.Text(true, provider.LastSuccessfulUsageFetchUtc, DateTime.UtcNow)
            : provider.OldReadingUtc is { } reading ? $"Reading is {Ago(reading).Replace(" ago", " old")} · fetched {Ago(provider.FetchedAtUtc)}"
            : !string.IsNullOrWhiteSpace(provider.Error) ? $"Checked {Ago(provider.FetchedAtUtc)}"   // the error banner says what failed
            : $"Updated {Ago(provider.FetchedAtUtc)}";
        return string.IsNullOrWhiteSpace(provider.Source) ? text : $"{text} · via {provider.Source}";
    }

    /// <summary>Tooltip for the freshness line: exact times and what "via" means.</summary>
    public static string FreshnessTip(DashboardProvider? provider) => provider == null ? string.Empty
        : string.Join('\n', new[]
        {
            $"Fetched {Local(provider.FetchedAtUtc)}",
            provider.OldReadingUtc is { } reading ? $"The numbers were read by the provider on {Local(reading)}" : null,
            provider.StatusIsStale ? StaleCaption.StatusText(true, provider.LastSuccessfulStatusFetchUtc, DateTime.UtcNow) : null,
            string.IsNullOrWhiteSpace(provider.Source) ? null : $"Source: {provider.Source} (where the CLI read these numbers)"
        }.Where(line => !string.IsNullOrWhiteSpace(line)));

    /// <summary>Normal freshness text, or collapsed when the stale (amber) variant shows instead.</summary>
    public static Visibility VisFresh(DashboardProvider? provider) => Vis(provider != null && !DashboardGlance.IsStale(provider));
    public static Visibility VisStale(DashboardProvider? provider) => Vis(DashboardGlance.IsStale(provider));

    /// <summary>The header's service status: shown only when the provider reported a known status.</summary>
    public static Visibility VisStatus(ProviderStatus? status) => Vis(status is { Indicator: not ProviderStatusIndicator.Unknown });
    public static Visibility VisStatusLink(ProviderStatus? status) => Vis(VisStatus(status) == Visibility.Visible && StatusUri(status) != null);
    public static Visibility VisStatusPlain(ProviderStatus? status) => Vis(VisStatus(status) == Visibility.Visible && StatusUri(status) == null);
    /// <summary>Status dot tooltip: detail plus when the status page was last updated (or that it is last-known).</summary>
    public static string StatusHeaderTip(DashboardProvider? provider) => provider == null ? string.Empty
        : string.Join('\n', new[] { StatusTip(provider), StatusChecked(provider) }.Where(line => !string.IsNullOrWhiteSpace(line)).Distinct());

    /// <summary>The incident banner only shows when there is incident detail beyond the header's status description.</summary>
    public static bool HasIncident(DashboardProvider? provider) =>
        provider != null && ProviderStatusPresentation.ShowBanner(provider.ServiceStatus) &&
        (!string.IsNullOrWhiteSpace(IncidentTitle(provider)) || !string.IsNullOrWhiteSpace(IncidentBody(provider)));
    public static Visibility VisIncident(DashboardProvider? provider) => Vis(HasIncident(provider));
    public static string IncidentHeading(DashboardProvider? provider) =>
        VisIncidentTitle(provider) == Visibility.Visible ? IncidentTitle(provider) : StatusText(provider?.ServiceStatus);
    public static Microsoft.UI.Xaml.Controls.InfoBarSeverity IncidentSeverity(ProviderStatus? status) =>
        ProviderStatusPresentation.Severity(status) == ProviderStatusSeverity.Error
            ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
    public static bool HasText(string? text) => !string.IsNullOrWhiteSpace(text);

    // Usage window rows
    /// <summary>The reset caption for a window, or for the last window of a group sharing one reset ("Both reset in 7h 3m · Thu 15 Oct, 9:00 PM").</summary>
    public static string ResetCaption(DashboardWindow? window, int groupSize)
    {
        if (window == null) return string.Empty;
        if (groupSize <= 1 || window.ResetsAtUtc is not { } reset) return ResetLine(window.ResetText, window.ResetsAtUtc);
        var when = In(reset) switch { "now" => "now", "<1m" => "in under a minute", var text => text };
        return $"{DashboardGlance.ResetVerb(groupSize)} {when} · {Local(reset)}";
    }
    /// <summary>The pace line shows up front only when the window is on course to run out; otherwise it waits in the details.</summary>
    public static bool IsPaceAlert(DashboardWindow? window) =>
        window is { Pace.Kind: PaceKind.WillRunOut or PaceKind.LimitReached } && !string.IsNullOrWhiteSpace(window.PaceSummary);
    public static Visibility VisPaceAlert(DashboardWindow? window) => Vis(IsPaceAlert(window));
    public static Visibility VisPaceDetail(DashboardWindow? window, bool details) =>
        Vis(details && window != null && !IsPaceAlert(window) && !string.IsNullOrWhiteSpace(window.PaceSummary));
    /// <summary>"5-hour window · 62% of the time has passed · last moved 2m ago".</summary>
    public static string WindowDetail(DashboardWindow? window) => window == null ? string.Empty
        : window.LastChangedAtUtc is { } moved
            ? $"{WindowMeta(window.WindowMinutes, window.ElapsedPercent)} · last moved {Ago(moved)}"
            : WindowMeta(window.WindowMinutes, window.ElapsedPercent);
    public static Visibility VisSeriesReady(BurndownSeries? series) => Vis(series is { IsReady: true });
    public static Visibility VisSeriesMissing(BurndownSeries? series) => Vis(series is not { IsReady: true });
    /// <summary>One line instead of an empty chart box: "No history chart yet: waiting for window reset (0 samples)".</summary>
    public static string NoSeriesText(BurndownSeries? series)
    {
        var samples = Plural(series?.SampleCount ?? 0, "sample");
        var reason = series?.Message;
        return string.IsNullOrWhiteSpace(reason)
            ? $"No history chart yet ({samples})"
            : $"No history chart yet: {char.ToLowerInvariant(reason[0])}{reason[1..]} ({samples})";
    }

    /// <summary>Caption under the cost heading when totals are last-known-good; empty when fresh.</summary>
    public static string CostStale(DashboardCost? cost) => cost == null ? string.Empty
        : StaleCaption.Text(cost.IsStale, cost.LastSuccessfulFetchUtc, DateTime.UtcNow);
    public static Visibility VisCostStale(DashboardCost? cost) => VisText(CostStale(cost));

    // Claude Platform API credits (Data.ApiCredits); every helper tolerates null so x:Bind can pass the property directly.
    public static Visibility VisApiOk(ApiCreditsInfo? info) => Vis(info is { State: ApiCreditsState.Ok });
    public static Visibility VisApiMessage(ApiCreditsInfo? info) => info is { State: not ApiCreditsState.Ok and not ApiCreditsState.NoKey } ? VisText(info.Message) : Visibility.Collapsed;
    public static Visibility VisApiKeyLink(ApiCreditsInfo? info) => Vis(info is { State: ApiCreditsState.NoKey or ApiCreditsState.Rejected });
    public static Visibility VisApiNoKey(ApiCreditsInfo? info) => Vis(info is { State: ApiCreditsState.NoKey });
    public static Visibility VisApiStale(ApiCreditsInfo? info) => Vis(info is { State: ApiCreditsState.Ok, IsStale: true });
    public static Visibility VisApiPace(ApiCreditsInfo? info) => Vis(info is { State: ApiCreditsState.Ok, PaceUsdPerDay: > 0 });
    public static string ApiSpent(ApiCreditsInfo? info) => info == null ? string.Empty : Money(info.SpentUsd, "USD");
    public static string ApiOf(ApiCreditsInfo? info) => info == null ? string.Empty : $"of {Money(info.MonthlyUsd, "USD")}";
    public static string ApiRemaining(ApiCreditsInfo? info) => info == null ? string.Empty
        : info.IsOverBudget || info.RemainingUsd < 0 ? $"Over budget by {Money(-info.RemainingUsd, "USD")}" : Money(info.RemainingUsd, "USD");
    public static string ApiResets(ApiCreditsInfo? info)
    {
        if (info == null) return string.Empty;
        var resetUtc = DateTime.SpecifyKind(info.CycleEnd.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(resetUtc, TimeZoneInfo.Local);
        return $"{local.ToString("ddd d MMM", CultureInfo.CurrentCulture)} · in {Plural(Math.Max(1, info.DaysLeft), "day")}";
    }
    public static string ApiPace(ApiCreditsInfo? info) => info == null ? string.Empty : $"{Money(info.PaceUsdPerDay, "USD")} / day to use it all";
    public static string ApiUpdated(ApiCreditsInfo? info) => info?.FetchedAtUtc is { } at ? $"{Local(at)} · {Ago(at)}" : "—";
    public static string ApiAutomationName(ApiCreditsInfo? info) => info is { State: ApiCreditsState.Ok }
        ? $"API credits: {ApiSpent(info)} {ApiOf(info)}, {info.PercentUsed:F0}% used" : "API credits";
    public static Visibility VisApiBreakdown(ApiCreditsInfo? info) => Vis(info is { State: ApiCreditsState.Ok, HasBreakdown: true });
    public static Visibility VisApiUnattributed(ApiCreditsInfo? info) => Vis(info is { State: ApiCreditsState.Ok, HasBreakdown: true, UnattributedUsd: >= 0.005m });
    public static string ApiUnattributed(ApiCreditsInfo? info) => info == null ? string.Empty : Money(info.UnattributedUsd, "USD");
    /// <summary>A member's share of the pool: "0%" for nothing, "&lt;1%" for a non-zero amount under one percent, else rounded.</summary>
    public static string ApiShare(decimal spent, decimal pool)
    {
        if (spent <= 0 || pool <= 0) return "0%";
        var percent = spent / pool * 100;
        return percent < 1 ? "<1%" : $"{decimal.Round(percent, MidpointRounding.AwayFromZero):0}%";
    }
    /// <summary>The API section: hidden without data; the "no key" how-to only shows with the panel's details open.</summary>
    public static Visibility VisApiSection(ApiCreditsInfo? info, bool details) =>
        Vis(info != null && (info.State != ApiCreditsState.NoKey || details));
    public static string ApiLeft(ApiCreditsInfo? info) => info == null ? string.Empty
        : info.IsOverBudget || info.RemainingUsd < 0 ? $"Over by {Money(-info.RemainingUsd, "USD")}" : $"{Money(info.RemainingUsd, "USD")} left";
    public static Visibility VisApiLeftOk(ApiCreditsInfo? info) => Vis(info is { State: ApiCreditsState.Ok, IsOverBudget: false, RemainingUsd: >= 0 });
    public static Visibility VisApiLeftOver(ApiCreditsInfo? info) => Vis(info is { State: ApiCreditsState.Ok } && (info.IsOverBudget || info.RemainingUsd < 0));
    /// <summary>"Resets Sat 1 Nov, in 17 days · $6.54/day to use it all · updated just now".</summary>
    public static string ApiCaption(ApiCreditsInfo? info)
    {
        if (info == null) return string.Empty;
        var parts = new List<string> { $"Resets {ApiResets(info).Replace(" · in ", ", in ")}" };
        if (info.PaceUsdPerDay > 0) parts.Add($"{Money(info.PaceUsdPerDay, "USD")}/day to use it all");
        if (!info.IsStale && info.FetchedAtUtc is { } at) parts.Add($"updated {Ago(at)}");
        return string.Join(" · ", parts);
    }
    public static string ApiStaleText(ApiCreditsInfo? info) => info?.FetchedAtUtc is { } at
        ? $"Stale · last updated {Ago(at)} (the Anthropic API did not answer)" : "Stale · the Anthropic API did not answer";
    public static string ApiUpdatedTip(ApiCreditsInfo? info) => info?.FetchedAtUtc is { } at ? $"Read from the Anthropic Cost API at {Local(at)}" : "Anthropic Cost API";
    public static Uri ApiBillingUri() => new(ApiCreditsInfo.BillingUrl);
    public static Uri ApiAdminKeysUri() => new(ApiCreditsInfo.AdminKeysUrl);
    /// <summary>The one compact line shown without a key: what is missing and how to add it.</summary>
    public static string ApiNoKeyText(ApiCreditsInfo? info) => $"{info?.Message} {ApiNoKeyHelp()}".Trim();
    public static string ApiNoKeyHelp() =>
        $"Save it as the user environment variable {Services.ApiBillingService.AdminKeyEnvironmentVariable}. CodexBarWin picks it up on the next refresh (restart it if you replaced an existing key).";

    /// <summary>How far through the billing cycle (CycleStart to CycleEnd, whole UTC days) <paramref name="utcNow"/> is, 0-100; null when the cycle is unknown.</summary>
    public static double? ApiElapsedPercent(ApiCreditsInfo? info, DateTime utcNow)
    {
        if (info == null || info.CycleEnd <= info.CycleStart) return null;
        var start = info.CycleStart.ToDateTime(TimeOnly.MinValue);
        var end = info.CycleEnd.ToDateTime(TimeOnly.MinValue);
        var now = DashboardValues.Utc(utcNow);
        var fraction = (now - start).TotalDays / (end - start).TotalDays;
        return Math.Clamp(fraction * 100, 0, 100);
    }
    /// <summary>The API meter's time-elapsed layer, in the 0-100 / -1 form <c>UsageMeter.TimePercent</c> takes.</summary>
    public static double ApiTime(ApiCreditsInfo? info) => Time(ApiElapsedPercent(info, DateTime.UtcNow));

    // Summary "Needs attention" entries
    public static string AttentionText(string? label, string? detail) => $"{label}: {detail}";
    public static Visibility VisCritical(AttentionSeverity severity) => Vis(severity == AttentionSeverity.Critical);
    public static Visibility VisCaution(AttentionSeverity severity) => Vis(severity != AttentionSeverity.Critical);

    public static Brush OkBrush(bool ok) => ok ? Green : Red;
    public static string OkGlyph(bool ok) => ok ? "" : "";   // check / cancel
    public static string Duration(int ms) => ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.0} s";
    public static string Bytes(long bytes) => bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";
    public static string OnOff(bool on) => on ? "On" : "Off";
    public static Brush OnOffBrush(bool on) => on ? Green : Grey;
    public static string Seconds(int seconds) => seconds % 60 == 0 && seconds >= 60 ? $"{seconds / 60} min" : $"{seconds} s";
    public static string Unknown(string? text) => string.IsNullOrWhiteSpace(text) ? "Unknown" : text!;
    public static string Plural(int count, string singular, string? plural = null) =>
        $"{count} {(count == 1 ? singular : plural ?? singular + "s")}";
}
