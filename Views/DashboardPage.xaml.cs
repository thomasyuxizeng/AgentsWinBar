using CodexBarWin.Controls;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CodexBarWin.Views;

/// <summary>One headline number in the strip at the top of the dashboard.</summary>
public sealed record DashboardTile(string Label, string Value, string Sub, Brush Accent);

/// <summary>
/// The dashboard's content: toolbar, headline tiles, one panel per provider and the diagnostics column.
/// It only draws a <see cref="DashboardSnapshot"/>; fetching and timers live in <see cref="DashboardAppWindow"/>.
/// </summary>
public sealed partial class DashboardPage : Page
{
    private const double MinPanelWidth = 420;
    private const double ColumnGap = 14;

    private static readonly Brush Green = new SolidColorBrush(Color.FromArgb(255, 16, 163, 127));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromArgb(255, 255, 185, 0));
    private static readonly Brush Red = new SolidColorBrush(Color.FromArgb(255, 232, 17, 35));
    private static readonly Brush Blue = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
    private static readonly Brush Neutral = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));

    private readonly Dictionary<string, DashboardProviderPanel> _panels = new();
    private int _arrangedColumns = -1;
    private string _arrangedOrder = string.Empty;

    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler<bool>? TopmostChanged;
    public event EventHandler<bool>? FullScreenChanged;

    public DashboardPage()
    {
        InitializeComponent();
    }

    public void SetBusy(bool busy)
    {
        RefreshRing.IsActive = busy;
        RefreshRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = !busy;
    }

    public void SetFullScreen(bool on) => FullScreenToggle.IsChecked = on;

    public void Update(DashboardSnapshot snapshot)
    {
        var summary = snapshot.Summary;
        SubtitleText.Text = summary.LastRefreshUtc.HasValue
            ? $"Updated {DashboardFormat.LocalTime(summary.LastRefreshUtc.Value)} ({DashboardFormat.Ago(summary.LastRefreshUtc.Value)})" +
              (summary.NextRefreshUtc.HasValue ? $" · next refresh {DashboardFormat.In(summary.NextRefreshUtc)}" : string.Empty) +
              $" · every {DashboardFormat.Seconds(summary.RefreshIntervalSeconds)}"
            : "Waiting for the first refresh…";

        TilesRepeater.ItemsSource = BuildTiles(snapshot);
        Diagnostics.Data = snapshot.Diagnostics;
        UpdateProviders(snapshot.Providers);
    }

    private IReadOnlyList<DashboardTile> BuildTiles(DashboardSnapshot snapshot)
    {
        var s = snapshot.Summary;
        var tiles = new List<DashboardTile>();

        tiles.Add(new("PROVIDERS", $"{s.HealthyProviderCount} of {s.ProviderCount} healthy",
            s.ErrorProviderCount + s.StaleProviderCount == 0 ? "All reporting normally"
                : $"{DashboardFormat.Plural(s.ErrorProviderCount, "error")} · {s.StaleProviderCount} stale",
            s.ErrorProviderCount > 0 ? Red : s.StaleProviderCount > 0 ? Amber : Green));

        tiles.Add(new("SERVICE STATUS", s.IncidentCount == 0 ? "No incidents" : DashboardFormat.Plural(s.IncidentCount, "incident"),
            "From each provider's status page", s.IncidentCount > 0 ? Red : Green));

        tiles.Add(new("LIMITS", $"{s.WindowsAtLimit} at · {s.WindowsNearLimit} near",
            $"{s.WindowsAheadOfPace} ahead of pace · {DashboardFormat.Plural(s.WindowCount, "window")} tracked",
            s.WindowsAtLimit > 0 ? Red : s.WindowsNearLimit > 0 || s.WindowsAheadOfPace > 0 ? Amber : Green));

        tiles.Add(s.MostUsedWindow is { } most
            ? new("MOST USED", $"{most.UsedPercent:F0}%", $"{most.ProviderName} · {most.WindowTitle} · resets {DashboardFormat.In(most.ResetsAtUtc)}",
                most.UsedPercent >= 95 ? Red : most.UsedPercent >= 80 ? Amber : Blue)
            : new("MOST USED", "—", "No usage windows yet", Neutral));

        tiles.Add(s.NextResetWindow is { } next
            ? new("NEXT RESET", DashboardFormat.In(next.ResetsAtUtc), $"{next.ProviderName} · {next.WindowTitle} · {DashboardFormat.Local(next.ResetsAtUtc)}", Blue)
            : new("NEXT RESET", "—", "No reset times reported", Neutral));

        tiles.Add(s.CostAvailable
            ? new("COST TODAY", DashboardFormat.Money(s.TotalCostToday, s.CostCurrency),
                $"{DashboardFormat.Tokens(s.TotalTokensToday)} tokens · estimated", Blue)
            : new("COST TODAY", "—", "Cost totals are off or not scanned yet", Neutral));

        tiles.Add(s.CostAvailable
            ? new("COST · 30 DAYS", DashboardFormat.Money(s.TotalCost30Days, s.CostCurrency),
                $"{DashboardFormat.Tokens(s.TotalTokens30Days)} tokens · all providers", Blue)
            : new("COST · 30 DAYS", "—", "Enable cost totals in Settings", Neutral));

        return tiles;
    }

    private void UpdateProviders(IReadOnlyList<DashboardProvider> providers)
    {
        var ids = providers.Select(p => p.Id).ToList();

        foreach (var stale in _panels.Keys.Where(k => !ids.Contains(k)).ToList()) _panels.Remove(stale);
        foreach (var provider in providers)
        {
            if (!_panels.TryGetValue(provider.Id, out var panel))
            {
                panel = new DashboardProviderPanel { VerticalAlignment = VerticalAlignment.Top };
                _panels[provider.Id] = panel;
            }
            panel.Data = provider;
        }

        EmptyPanel.Visibility = providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Arrange(force: _arrangedOrder != string.Join('|', ids));
    }

    private void OnScrollSizeChanged(object sender, SizeChangedEventArgs e) => Arrange(force: false);

    private void OnLayoutStateChanged(object sender, VisualStateChangedEventArgs e) => Arrange(force: false);

    /// <summary>Lays the provider panels out in as many columns as fit (each at least <see cref="MinPanelWidth"/> wide).</summary>
    private void Arrange(bool force)
    {
        // Same threshold as the AdaptiveTrigger, read directly so it never lags behind the visual state
        var wide = (XamlRoot?.Size.Width ?? 0) >= 1500;
        var diagnosticsWidth = wide ? 380 + ColumnGap : 0;
        var available = Math.Max(0, MainScroll.ActualWidth - 12 - diagnosticsWidth);
        var count = Math.Max(1, _panels.Count);
        var columns = Math.Clamp((int)Math.Floor((available + ColumnGap) / (MinPanelWidth + ColumnGap)), 1, count);

        var order = _panels.Keys.ToList();
        var orderKey = string.Join('|', order);
        if (!force && columns == _arrangedColumns && orderKey == _arrangedOrder) return;

        _arrangedColumns = columns;
        _arrangedOrder = orderKey;
        ProvidersGrid.Children.Clear();
        ProvidersGrid.ColumnDefinitions.Clear();
        ProvidersGrid.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++)
            ProvidersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = (int)Math.Ceiling(_panels.Count / (double)columns);
        for (var r = 0; r < rows; r++)
            ProvidersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < order.Count; i++)
        {
            var panel = _panels[order[i]];
            Grid.SetRow(panel, i / columns);
            Grid.SetColumn(panel, i % columns);
            ProvidersGrid.Children.Add(panel);
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    private void OnSettingsClicked(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void OnTopmostClicked(object sender, RoutedEventArgs e) => TopmostChanged?.Invoke(this, TopmostToggle.IsChecked == true);
    private void OnFullScreenClicked(object sender, RoutedEventArgs e) => FullScreenChanged?.Invoke(this, FullScreenToggle.IsChecked == true);
}
