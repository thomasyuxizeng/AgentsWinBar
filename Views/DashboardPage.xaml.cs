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
/// The dashboard's content: toolbar, headline tiles, one panel per provider and the diagnostics cards below them.
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
    private readonly List<StackPanel> _columnStacks = new();
    private DashboardSnapshot? _snapshot;
    private List<DashboardTile> _tiles = [];
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
        _snapshot = snapshot;
        UpdateSubtitle(snapshot.Summary);

        UpdateTiles(snapshot);
        if (!DashboardContent.DiagnosticsEquals(Diagnostics.Data, snapshot.Diagnostics)) Diagnostics.Data = snapshot.Diagnostics;
        else Diagnostics.RefreshTimes();
        Overview.Update(snapshot.Providers);
        UpdateProviders(snapshot.Providers);
    }

    public void RefreshTimes()
    {
        if (_snapshot == null) return;
        UpdateSubtitle(_snapshot.Summary);
        UpdateTiles(_snapshot);
        Overview.RefreshTimes();
        foreach (var panel in _panels.Values) panel.RefreshTimes();
        Diagnostics.RefreshTimes();
    }

    private void UpdateTiles(DashboardSnapshot snapshot)
    {
        // Seven small tiles: when any value changed, hand the repeater the new list (cheap, and always correct)
        var tiles = BuildTiles(snapshot);
        if (_tiles.Count == tiles.Count && _tiles.SequenceEqual(tiles)) return;
        TilesRepeater.ItemsSource = _tiles = tiles.ToList();
    }

    private void UpdateSubtitle(DashboardSummary summary)
    {
        SubtitleText.Text = summary.LastRefreshUtc.HasValue
            ? $"Updated {DashboardFormat.LocalTime(summary.LastRefreshUtc.Value)} ({DashboardFormat.Ago(summary.LastRefreshUtc.Value)})" +
              (summary.NextRefreshUtc.HasValue ? $" · next refresh {DashboardFormat.In(summary.NextRefreshUtc)}" : string.Empty) +
              (summary.EffectiveRefreshIntervalSeconds > summary.RefreshIntervalSeconds
                  ? $" · every {DashboardFormat.Seconds(summary.EffectiveRefreshIntervalSeconds)} while usage is idle"
                  : $" · every {DashboardFormat.Seconds(summary.RefreshIntervalSeconds)}")
            : "Waiting for the first refresh…";

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
                most.UsedPercent >= UsageThresholds.Critical ? Red : most.UsedPercent >= UsageThresholds.Warning ? Amber : Blue)
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
            if (!DashboardContent.ProviderEquals(panel.Data, provider)) panel.Data = provider;
            else panel.RefreshTimes();
        }

        EmptyPanel.Visibility = providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Arrange(force: false);
    }

    private void OnScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try { Arrange(force: false); }
        catch (Exception ex) { App.LogHandled("Dashboard layout update failed", ex); }
    }

    /// <summary>Lays the provider panels out in as many columns as fit (each at least <see cref="MinPanelWidth"/> wide).</summary>
    private void Arrange(bool force)
    {
        var available = Math.Max(0, MainScroll.ActualWidth - 12);
        var count = Math.Max(1, _panels.Count);
        var columns = Math.Clamp((int)Math.Floor((available + ColumnGap) / (MinPanelWidth + ColumnGap)), 1, count);

        var order = _panels.Keys.ToList();
        // Panels with an External pool (Gemini) show two window columns of their own, so with two or more
        // columns they take a full-width row below the others instead of sharing a narrow column
        var spanning = columns >= 2 ? order.Where(id => SpansRow(_panels[id].Data)).ToList() : [];
        // Never more columns than panels that share them (two regular panels never get an empty third column)
        columns = Math.Max(1, Math.Min(columns, order.Count - spanning.Count));
        var orderKey = string.Join('|', order) + "#" + string.Join('|', spanning);
        if (!force && columns == _arrangedColumns && orderKey == _arrangedOrder) return;

        _arrangedColumns = columns;
        _arrangedOrder = orderKey;
        // Release the panels from their previous column before re-parenting them
        foreach (var stack in _columnStacks) stack.Children.Clear();
        _columnStacks.Clear();
        ProvidersGrid.Children.Clear();
        ProvidersGrid.ColumnDefinitions.Clear();
        ProvidersGrid.RowDefinitions.Clear();
        ProvidersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var regular = order.Where(id => !spanning.Contains(id)).ToList();

        // Balanced columns: each panel goes into the currently shortest column, so a tall panel next to a short
        // one doesn't leave a hole (three providers in two columns become tall | short + short)
        var heights = new double[columns];
        for (var c = 0; c < columns; c++)
        {
            ProvidersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var stack = new StackPanel { Spacing = ColumnGap, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(stack, c);
            _columnStacks.Add(stack);
            ProvidersGrid.Children.Add(stack);
        }
        foreach (var id in regular)
        {
            var panel = _panels[id];
            var shortest = Array.IndexOf(heights, heights.Min());
            _columnStacks[shortest].Children.Add(panel);
            heights[shortest] += EstimateHeight(panel.Data);
        }
        foreach (var id in spanning)
        {
            ProvidersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var panel = _panels[id];
            Grid.SetRow(panel, ProvidersGrid.RowDefinitions.Count - 1);
            Grid.SetColumnSpan(panel, columns);
            ProvidersGrid.Children.Add(panel);
        }
    }

    /// <summary>True for a provider whose windows split into its own models and an External pool.</summary>
    private static bool SpansRow(DashboardProvider? provider) =>
        provider != null && provider.Windows.Any(w => w.Kind == DashboardWindowKind.Extra && w.Accent == Helpers.WindowAccent.ClaudeAndOpenAi);

    /// <summary>A rough height for balancing columns (real heights aren't known before layout).</summary>
    private static double EstimateHeight(DashboardProvider? provider) =>
        provider == null ? 0 : 150 + provider.Windows.Count * 215 + (provider.Cost != null ? 640 : 0);

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    private void OnSettingsClicked(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void OnTopmostClicked(object sender, RoutedEventArgs e) => TopmostChanged?.Invoke(this, TopmostToggle.IsChecked == true);
    private void OnFullScreenClicked(object sender, RoutedEventArgs e) => FullScreenChanged?.Invoke(this, FullScreenToggle.IsChecked == true);
}
