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

    public static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisText(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisNotNull(object? value) => value == null ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisCount(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisEmpty(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public static string Percent(double value) => $"{FormatPercent(value):F0}%";
    public static string Used(double value) => $"{FormatPercent(value):F0}% used";
    private static double FormatPercent(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
    public static double Time(double? elapsedPercent) => elapsedPercent is { } value && double.IsFinite(value) ? DashboardValues.Percent(value) : -1;

    /// <summary>"Near limit" / "At limit" chip (75% / 90%, UsageThresholds, the same as the bars); hidden below 75%.</summary>
    public static string LimitLabel(double percent) => percent >= UsageThresholds.Critical ? "AT LIMIT" : percent >= UsageThresholds.Warning ? "NEAR LIMIT" : string.Empty;
    public static Visibility LimitVis(double percent) => Vis(percent >= UsageThresholds.Warning);
    /// <summary>"RESET EARLY" chip and its detail line for a window reset ahead of schedule (empty / collapsed otherwise).</summary>
    public static string EarlyChip(DashboardWindow? window) => EarlyResetDisplay.Chip(window?.EarlyResetAtUtc);
    public static Visibility EarlyVis(DashboardWindow? window) => Vis(window?.EarlyResetAtUtc != null);
    public static string EarlyDetail(DashboardWindow? window) => window == null ? string.Empty
        : EarlyResetDisplay.Detail(window.EarlyResetAtUtc, window.EarlyResetFromPercent, window.UsedPercent, DateTime.UtcNow);
    public static Visibility VisEarlyDetail(DashboardWindow? window) => VisText(EarlyDetail(window));
    public static Brush EarlyBrush() => Green;

    public static Brush LimitBrush(double percent) => percent >= UsageThresholds.Critical ? Red : Amber;

    public static string Money(decimal value, string currency) => CostFormatter.Money(value, currency);
    public static string Tokens(long tokens) => CostFormatter.Tokens(tokens);
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

    public static string Changed(DateTime? utc) => utc.HasValue ? $"Last moved {Ago(utc.Value)}" : string.Empty;

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
    public static Visibility StatusBanner(ProviderStatus? status) => Vis(ProviderStatusPresentation.ShowBanner(status));
    public static Uri? StatusUri(ProviderStatus? status) => ProviderStatusPresentation.StatusPageUri(status);
    public static Visibility StatusLink(ProviderStatus? status) => VisNotNull(ProviderStatusPresentation.StatusPageUri(status));

    public static Geometry Icon(string? providerId) => ProviderIcons.GetGeometry(providerId ?? string.Empty);
    public static string ProviderName(string providerId) => ProviderIcons.GetDisplayName(providerId);
    public static string RefreshTitle(int count) => count == 0 ? "REFRESH LOG" : $"REFRESH LOG · LAST {count}";

    public static string Freshness(DashboardProvider? provider) => provider == null ? string.Empty
        : provider.IsLoading ? "Refreshing…"
        : provider.UsageIsStale ? StaleCaption.Text(true, provider.LastSuccessfulUsageFetchUtc, DateTime.UtcNow)
        : provider.OldReadingUtc is { } reading
            ? $"Data from {Local(reading)} · {Ago(reading)} · fetched {Ago(provider.FetchedAtUtc)}"
            : $"Fetched {Local(provider.FetchedAtUtc)} · {Ago(provider.FetchedAtUtc)}";

    /// <summary>Caption under the cost heading when totals are last-known-good; empty when fresh.</summary>
    public static string CostStale(DashboardCost? cost) => cost == null ? string.Empty
        : StaleCaption.Text(cost.IsStale, cost.LastSuccessfulFetchUtc, DateTime.UtcNow);
    public static Visibility VisCostStale(DashboardCost? cost) => VisText(CostStale(cost));

    /// <summary>A short note for a provider card's header: what needs attention, or nothing.</summary>
    public static string CardNote(DashboardProvider? provider) => provider == null ? string.Empty
        : provider.IsLoading ? "Refreshing…"
        : !string.IsNullOrWhiteSpace(provider.Error) ? "Fetch failed"
        : provider.IsStale ? "Stale" : string.Empty;

    /// <summary>The overview line's tooltip: the window, its reset and the pace outlook.</summary>
    public static string MiniTip(DashboardWindow? window) => window == null ? string.Empty
        : string.Join('\n', new[]
        {
            $"{window.Title}: {FormatPercent(window.UsedPercent):F0}% used",
            ResetLine(window.ResetText, window.ResetsAtUtc),
            window.PaceSummary,
            EarlyDetail(window)
        }.Where(line => !string.IsNullOrWhiteSpace(line)));

    public static string SourceLine(string? source) => string.IsNullOrWhiteSpace(source) ? "Source unknown" : $"Source: {source}";

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
