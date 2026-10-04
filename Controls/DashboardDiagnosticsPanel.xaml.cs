using CodexBarWin.Models;
using CodexBarWin.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexBarWin.Controls;

/// <summary>The dashboard's right-hand column: system facts, effective settings, history store, cost scans, refresh log and alerts.</summary>
public sealed partial class DashboardDiagnosticsPanel : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data),
        typeof(DashboardDiagnostics), typeof(DashboardDiagnosticsPanel), new PropertyMetadata(new DashboardDiagnostics(), OnDataChanged));

    public DashboardDiagnostics Data
    {
        get => (DashboardDiagnostics)GetValue(DataProperty);
        set => SetValue(DataProperty, value with
        {
            RecentRefreshes = Data.RecentRefreshes.SequenceEqual(value.RecentRefreshes) ? Data.RecentRefreshes : value.RecentRefreshes,
            RecentAlerts = Data.RecentAlerts.SequenceEqual(value.RecentAlerts) ? Data.RecentAlerts : value.RecentAlerts
        });
    }

    public IReadOnlyList<DashboardFact> SystemFacts { get; private set; } = [];
    public IReadOnlyList<DashboardFact> SettingsFacts { get; private set; } = [];
    public IReadOnlyList<DashboardFact> HistoryFacts { get; private set; } = [];
    public IReadOnlyList<DashboardFact> CostCacheFacts { get; private set; } = [];

    public DashboardDiagnosticsPanel()
    {
        InitializeComponent();
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DashboardDiagnosticsPanel)d).Refresh();

    public void RefreshTimes()
    {
        DashboardTemplateBindings.Update(Card3, item =>
        {
            if (item is not DashboardFact fact) return item;
            var cache = Data.CostCache.FirstOrDefault(c => DashboardFormat.ProviderName(c.ProviderId) == fact.Label);
            return cache?.LastAttemptUtc is { } utc
                ? fact with { Value = $"{DashboardFormat.Ago(utc)} · {(cache.HasData ? "has data" : "no data")}" } : item;
        });
    }

    private void Refresh()
    {
        var d = Data ?? new DashboardDiagnostics();

        IReadOnlyList<DashboardFact> systemFacts =
        [
            new("App version", VersionText(d.AppVersion)),
            new("CodexBar CLI", d.CliAvailable switch
            {
                true => $"{DashboardFormat.Unknown(d.CliVersion)} · available",
                false => "Not found in WSL",
                _ => d.CliVersion ?? "Checking…"
            }),
            new("WSL distribution", string.IsNullOrWhiteSpace(d.WslDistro) ? "Default WSL distribution" : d.WslDistro),
            new("Settings file", d.SettingsPath)
        ];

        IReadOnlyList<DashboardFact> settingsFacts =
        [
            new("Theme", DashboardFormat.Unknown(d.Theme)),
            new("Refresh every", DashboardFormat.Seconds(d.RefreshIntervalSeconds)),
            new("Cache expires after", DashboardFormat.Plural(d.CacheExpiryMinutes, "minute")),
            new("WSL command limit", DashboardFormat.Seconds(d.WslCommandTimeoutSeconds)),
            new("CLI provider limit", DashboardFormat.Seconds(d.CliProviderTimeoutSeconds)),
            new("Other provider limit", DashboardFormat.Seconds(d.StandardProviderTimeoutSeconds)),
            new("Floating overlay", DashboardFormat.OnOff(d.OverlayEnabled)),
            new("Taskbar strip", DashboardFormat.OnOff(d.TaskbarStripEnabled)),
            new("Usage alerts", DashboardFormat.OnOff(d.AlertsEnabled)),
            new("Cost totals", DashboardFormat.OnOff(d.CostTotalsEnabled)),
            new("Start with Windows", DashboardFormat.OnOff(d.StartWithWindows)),
            new("Developer mode", DashboardFormat.OnOff(d.DeveloperModeEnabled))
        ];

        IReadOnlyList<DashboardFact> historyFacts =
        [
            new("Samples stored", d.HistorySampleCount.ToString("N0")),
            new("Oldest", DashboardFormat.Local(d.HistoryOldestUtc)),
            new("Newest", DashboardFormat.Local(d.HistoryNewestUtc)),
            new("File size", DashboardFormat.Bytes(d.HistoryFileBytes))
        ];

        var costCacheFacts = d.CostCache.Select(c => new DashboardFact(DashboardFormat.ProviderName(c.ProviderId),
            c.LastAttemptUtc.HasValue
                ? $"{DashboardFormat.Ago(c.LastAttemptUtc.Value)} · {(c.HasData ? "has data" : "no data")}"
                : "not scanned yet")).ToList();

        if (!SystemFacts.SequenceEqual(systemFacts)) SystemFacts = systemFacts;
        if (!SettingsFacts.SequenceEqual(settingsFacts)) SettingsFacts = settingsFacts;
        if (!HistoryFacts.SequenceEqual(historyFacts)) HistoryFacts = historyFacts;
        if (!CostCacheFacts.SequenceEqual(costCacheFacts)) CostCacheFacts = costCacheFacts;
        Bindings.Update();
    }

    private int _columns = -1;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Arrange();

    /// <summary>Flows the cards into as many columns as fit (a 380-wide side column is one; the full-width layout is up to three).</summary>
    private void Arrange()
    {
        var columns = Math.Clamp((int)Math.Floor((CardsGrid.ActualWidth + 12) / (340 + 12)), 1, 3);
        if (columns == _columns) return;
        _columns = columns;
        CardsGrid.ColumnDefinitions.Clear();
        CardsGrid.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++) CardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        UIElement[] cards = [Card0, Card1, Card2, Card3, Card4, Card5];
        for (var r = 0; r < (int)Math.Ceiling(cards.Length / (double)columns); r++) CardsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < cards.Length; i++)
        {
            Grid.SetRow((FrameworkElement)cards[i], i / columns);
            Grid.SetColumn((FrameworkElement)cards[i], i % columns);
        }
    }

    /// <summary>The installed package version plus the short build string, instead of a raw 40-character commit hash.</summary>
    private static string VersionText(string? build)
    {
        string? package = null;
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            package = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch { /* not packaged */ }
        var shortBuild = build;
        if (!string.IsNullOrEmpty(build) && build.IndexOf('+') is var plus and > 0)
            shortBuild = build[..plus] + "+" + build[(plus + 1)..Math.Min(build.Length, plus + 8)];
        return package != null ? $"{package} · build {shortBuild}" : shortBuild ?? "Unknown";
    }
}
