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
        set => SetValue(DataProperty, value);
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

    private void Refresh()
    {
        var d = Data ?? new DashboardDiagnostics();

        SystemFacts =
        [
            new("App version", DashboardFormat.Unknown(d.AppVersion)),
            new("CodexBar CLI", d.CliAvailable switch
            {
                true => $"{DashboardFormat.Unknown(d.CliVersion)} · available",
                false => "Not found in WSL",
                _ => d.CliVersion ?? "Checking…"
            }),
            new("WSL distribution", DashboardFormat.Unknown(d.WslDistro)),
            new("Settings file", d.SettingsPath)
        ];

        SettingsFacts =
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

        HistoryFacts =
        [
            new("Samples stored", d.HistorySampleCount.ToString("N0")),
            new("Oldest", DashboardFormat.Local(d.HistoryOldestUtc)),
            new("Newest", DashboardFormat.Local(d.HistoryNewestUtc)),
            new("File size", DashboardFormat.Bytes(d.HistoryFileBytes))
        ];

        CostCacheFacts = d.CostCache.Select(c => new DashboardFact(DashboardFormat.ProviderName(c.ProviderId),
            c.LastAttemptUtc.HasValue
                ? $"{DashboardFormat.Ago(c.LastAttemptUtc.Value)} · {(c.HasData ? "has data" : "no data")}"
                : "not scanned yet")).ToList();

        Bindings.Update();
    }
}
