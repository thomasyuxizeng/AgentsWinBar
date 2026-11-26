using AgentsWinBar.Controls;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AgentsWinBar.Views;

/// <summary>A provider's graphs switched on or off from the dashboard's graphs section.</summary>
public sealed record GraphVisibilityChange(string ProviderId, bool Visible);

/// <summary>
/// The dashboard's content: toolbar, an optional summary (cross-provider problems and spend), one panel per provider
/// side by side, and diagnostics in an expander. It only draws a <see cref="DashboardSnapshot"/>; fetching and timers
/// live in <see cref="DashboardAppWindow"/>.
/// </summary>
public sealed partial class DashboardPage : Page
{
    /// <summary>Three panels fit side by side at the default 1350 DIP window (content about 1290 wide).</summary>
    private const double MinPanelWidth = 380;
    private const double ColumnGap = 16;
    /// <summary>Below this width the spend block moves under the attention list instead of beside it.</summary>
    private const double SummarySideBySideWidth = 640;
    /// <summary>Room needed to show the spend (caption, total, 30-day line) on one line.</summary>
    private const double SpendOneLineWidth = 900;

    private readonly Dictionary<string, DashboardProviderPanel> _panels = new();
    private readonly List<StackPanel> _columnStacks = new();
    private readonly Dictionary<string, DashboardGraphCard> _graphCards = new();
    private readonly Dictionary<string, ToggleButton> _graphToggles = new();
    private readonly HashSet<string> _hiddenGraphs = new(StringComparer.OrdinalIgnoreCase);
    private string _graphsArranged = string.Empty;
    private DashboardSnapshot? _snapshot;
    private IReadOnlyList<AttentionItem> _attention = [];
    private DashboardDiagnostics? _pendingDiagnostics;
    private string _summaryLayout = string.Empty;
    private int _arrangedColumns = -1;
    private string _arrangedOrder = string.Empty;
    private bool _disposed;
    private bool? _isBusy;

    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler<bool>? TopmostChanged;
    public event EventHandler<bool>? FullScreenChanged;
    public event EventHandler<GraphVisibilityChange>? GraphVisibilityChanged;

    public DashboardSnapshot? CurrentSnapshot => _snapshot;

    public DashboardPage()
    {
        InitializeComponent();
        AttentionRepeater.ElementPrepared += OnAttentionElementPrepared;
        AttentionRepeater.ElementClearing += OnAttentionElementClearing;
        DiagnosticsExpander.Expanding += OnDiagnosticsExpanding;
    }

    private void OnAttentionElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (_disposed || args.Element is not HyperlinkButton hb) return;
        hb.Click -= OnAttentionClicked;
        hb.Click += OnAttentionClicked;
    }

    private void OnAttentionElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is HyperlinkButton hb) hb.Click -= OnAttentionClicked;
    }

    private void DetachAttentionClicks(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is HyperlinkButton hb) hb.Click -= OnAttentionClicked;
            DetachAttentionClicks(child);
        }
    }

    /// <summary>Recursively tears down all child panels, charts, toggles, repeaters, and event subscriptions on window close.</summary>
    public void Cleanup()
    {
        if (_disposed) return;
        _disposed = true;
        MainScroll.SizeChanged -= OnScrollSizeChanged;
        RefreshButton.Click -= OnRefreshClicked;
        TopmostToggle.Click -= OnTopmostClicked;
        FullScreenToggle.Click -= OnFullScreenClicked;
        SettingsButton.Click -= OnSettingsClicked;

        SetBusy(false);
        AttentionRepeater.ElementPrepared -= OnAttentionElementPrepared;
        AttentionRepeater.ElementClearing -= OnAttentionElementClearing;
        DetachAttentionClicks(AttentionRepeater);
        // Clearing ItemsSource releases the items. Never set ItemTemplate to null: ItemsRepeater rejects it
        // (E_INVALIDARG), and the throw skipped the rest of this cleanup.
        AttentionRepeater.ItemsSource = null;
        AttentionPanel.Children.Clear();
        SummaryGrid.Children.Clear();
        SummaryCard.Child = null;
        _attention = [];
        _snapshot = null;

        foreach (var panel in _panels.Values) panel.Cleanup();
        _panels.Clear();
        foreach (var stack in _columnStacks) stack.Children.Clear();
        _columnStacks.Clear();
        ProvidersGrid.Children.Clear();
        ProvidersGrid.ColumnDefinitions.Clear();

        foreach (var card in _graphCards.Values) card.Cleanup();
        _graphCards.Clear();
        foreach (var toggle in _graphToggles.Values) toggle.Click -= OnGraphToggleClicked;
        _graphToggles.Clear();
        GraphToggles.Children.Clear();
        GraphsGrid.Children.Clear();
        GraphsGrid.ColumnDefinitions.Clear();
        GraphsGrid.RowDefinitions.Clear();

        DiagnosticsExpander.Expanding -= OnDiagnosticsExpanding;
        DiagnosticsExpander.IsExpanded = false;
        _pendingDiagnostics = null;
        Diagnostics.Cleanup();
        DiagnosticsExpander.Content = null;
        DiagnosticsExpander.Header = null;
        ContentStack.Children.Clear();
        _isBusy = null;
        DataContext = null;
    }

    public void SetBusy(bool busy)
    {
        if (_isBusy == busy) return;
        _isBusy = busy;
        RefreshRing.IsActive = busy;
        RefreshRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = !busy;
    }

    public void SetFullScreen(bool on)
    {
        if (!_disposed) FullScreenToggle.IsChecked = on;
    }

    public void Update(DashboardSnapshot snapshot)
    {
        if (_disposed) return;
        _snapshot = snapshot;
        UpdateSubtitle(snapshot.Summary);
        UpdateSummary(snapshot);
        if (DiagnosticsExpander.IsExpanded)
        {
            _pendingDiagnostics = null;
            if (!DashboardContent.DiagnosticsEquals(Diagnostics.Data, snapshot.Diagnostics)) Diagnostics.Data = snapshot.Diagnostics;
            else Diagnostics.RefreshTimes();
        }
        else
        {
            _pendingDiagnostics = snapshot.Diagnostics;
        }
        UpdateDiagnosticsSummary(snapshot.Diagnostics);
        UpdateProviders(snapshot.Providers);
        UpdateGraphs(snapshot.Providers);
    }

    public void ApplyClockTick(DashboardSnapshot updated)
    {
        if (_disposed) return;
        _snapshot = updated;
        UpdateSubtitle(updated.Summary);
        UpdateSummary(updated);
        foreach (var provider in updated.Providers)
        {
            if (_panels.TryGetValue(provider.Id, out var panel))
                panel.ApplyClockTick(provider);
        }
        if (DiagnosticsExpander.IsExpanded) Diagnostics.RefreshTimes();
    }

    private void OnDiagnosticsExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (_disposed || _pendingDiagnostics is not { } pending) return;
        _pendingDiagnostics = null;
        try
        {
            if (!DashboardContent.DiagnosticsEquals(Diagnostics.Data, pending)) Diagnostics.Data = pending;
            else Diagnostics.RefreshTimes();
        }
        catch (Exception ex) { App.LogHandled("Dashboard diagnostics expand failed", ex); }
    }

    /// <summary>The providers whose graphs the user switched off (from settings); redraws the graphs section when it changed.</summary>
    public void SetHiddenGraphs(IEnumerable<string> providerIds)
    {
        if (_disposed) return;
        var hidden = new HashSet<string>(providerIds, StringComparer.OrdinalIgnoreCase);
        if (hidden.SetEquals(_hiddenGraphs)) return;
        _hiddenGraphs.Clear();
        _hiddenGraphs.UnionWith(hidden);
        if (_snapshot != null) UpdateGraphs(_snapshot.Providers);
    }

    public void RefreshTimes()
    {
        if (_disposed || _snapshot == null) return;
        UpdateSubtitle(_snapshot.Summary);
        foreach (var panel in _panels.Values) panel.RefreshTimes();
        if (DiagnosticsExpander.IsExpanded) Diagnostics.RefreshTimes();
    }

    /// <summary>
    /// The toolbar's "Updated ... · next refresh ... · every ..." line. It is redrawn by the dashboard window's 30-second
    /// clock (a fresh snapshot each tick, stopped while the window is minimised or closed), so the relative times tick.
    /// Each part keeps its words together (non-breaking spaces): in a narrow window the line wraps at a separator onto a
    /// second line, and only beyond two lines is the end trimmed, with the whole line in the tooltip.
    /// </summary>
    private void UpdateSubtitle(DashboardSummary summary)
    {
        var parts = new List<string>(3);
        if (summary.LastRefreshUtc is { } last)
        {
            parts.Add($"Updated {DashboardFormat.LocalTime(last)} ({DashboardFormat.Ago(last)})");
            if (summary.NextRefreshUtc.HasValue) parts.Add($"next refresh {DashboardFormat.In(summary.NextRefreshUtc)}");
            parts.Add(summary.EffectiveRefreshIntervalSeconds > summary.RefreshIntervalSeconds
                ? $"every {DashboardFormat.Seconds(summary.EffectiveRefreshIntervalSeconds)} while usage is idle"
                : $"every {DashboardFormat.Seconds(summary.RefreshIntervalSeconds)}");
        }
        else parts.Add("Waiting for the first refresh…");
        var full = string.Join(" · ", parts);
        if (SubtitleText.Text.Length > 0 && string.Equals(ToolTipService.GetToolTip(SubtitleText) as string, full, StringComparison.Ordinal)) return;
        // Non-breaking inside a part and before each dot, a normal space after it: lines break only after "·"
        SubtitleText.Text = string.Join(NoBreakSpace + "· ", parts.Select(p => p.Replace(' ', NoBreakSpace)));
        ToolTipService.SetToolTip(SubtitleText, full);
    }

    private const char NoBreakSpace = ' ';

    /// <summary>
    /// The summary card: what needs attention across providers (each entry scrolls to its provider) and, when two or more
    /// providers report cost, the combined spend. With nothing to say the card collapses and the toolbar shows "All clear".
    /// </summary>
    private void UpdateSummary(DashboardSnapshot snapshot)
    {
        var attention = DashboardGlance.Attention(snapshot.Providers);
        if (!_attention.SequenceEqual(attention))
        {
            _attention = attention;
            AttentionRepeater.ItemsSource = attention.ToList();
        }

        var spend = snapshot.Summary.CostAvailable && DashboardGlance.ShowCombinedSpend(snapshot.Providers);
        if (spend)
        {
            var s = snapshot.Summary;
            var today = DashboardFormat.Money(s.TotalCostToday, s.CostCurrency);
            if (!string.Equals(SpendToday.Text, today, StringComparison.Ordinal))
                SpendToday.Text = today;
            var month = $"{DashboardFormat.Money(s.TotalCost30Days, s.CostCurrency)} in the last 30 days · " +
                        $"{DashboardFormat.Tokens(s.TotalTokens30Days)} tokens";
            if (!string.Equals(SpendMonth.Text, month, StringComparison.Ordinal))
                SpendMonth.Text = month;
        }

        var attentionVis = attention.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (AttentionPanel.Visibility != attentionVis) AttentionPanel.Visibility = attentionVis;
        var spendVis = spend ? Visibility.Visible : Visibility.Collapsed;
        if (SpendPanel.Visibility != spendVis) SpendPanel.Visibility = spendVis;
        var cardVis = attention.Count > 0 || spend ? Visibility.Visible : Visibility.Collapsed;
        if (SummaryCard.Visibility != cardVis) SummaryCard.Visibility = cardVis;
        var allClearVis = attention.Count == 0 && snapshot.Providers.Count > 0 && snapshot.Summary.LastRefreshUtc.HasValue
            ? Visibility.Visible : Visibility.Collapsed;
        if (AllClear.Visibility != allClearVis) AllClear.Visibility = allClearVis;
        ArrangeSummary();
    }

    /// <summary>Spend beside the attention list when there is room (or when it is alone), under it otherwise.</summary>
    private void ArrangeSummary()
    {
        var alone = AttentionPanel.Visibility != Visibility.Visible;
        // Alone, the spend reads as one line instead of a three-line block in an otherwise empty card
        var oneLine = alone && MainScroll.ActualWidth >= SpendOneLineWidth;
        var sideBySide = alone || MainScroll.ActualWidth >= SummarySideBySideWidth;
        var spendVis = SpendPanel.Visibility;
        var key = $"{alone}|{oneLine}|{sideBySide}|{spendVis}";
        if (string.Equals(_summaryLayout, key, StringComparison.Ordinal)) return;
        _summaryLayout = key;
        SpendPanel.Orientation = oneLine ? Orientation.Horizontal : Orientation.Vertical;
        SpendPanel.Spacing = oneLine ? 16 : 2;
        SummaryGrid.RowSpacing = sideBySide ? 0 : 12;
        Grid.SetColumn(SpendPanel, sideBySide && !alone ? 1 : 0);
        Grid.SetRow(SpendPanel, sideBySide ? 0 : 1);
        Grid.SetColumnSpan(AttentionPanel, sideBySide && spendVis == Visibility.Visible ? 1 : 2);
    }

    private void UpdateDiagnosticsSummary(DashboardDiagnostics d)
    {
        var version = string.IsNullOrWhiteSpace(d.AppVersion) ? null : d.AppVersion.Split('+')[0];
        var failed = d.RecentRefreshes.Count(r => !r.Success);
        var parts = new List<string?>
        {
            version != null ? $"App {version}" : null,
            d.CliAvailable switch { true => $"CLI {DashboardFormat.Unknown(d.CliVersion)}", false => "CLI not found in WSL", _ => null },
            d.RecentRefreshes.Count == 0 ? "no refreshes logged yet"
                : DashboardFormat.Plural(d.RecentRefreshes.Count, "refresh", "refreshes") + " logged" + (failed > 0 ? $" ({failed} failed)" : string.Empty),
            d.RecentAlerts.Count == 0 ? "no alerts this session" : DashboardFormat.Plural(d.RecentAlerts.Count, "alert") + " this session",
            "system, settings in effect, history store, refresh log"
        };
        var text = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (!string.Equals(DiagnosticsSummary.Text, text, StringComparison.Ordinal))
            DiagnosticsSummary.Text = text;
    }

    private void UpdateProviders(IReadOnlyList<DashboardProvider> providers)
    {
        var ids = providers.Select(p => p.Id).ToList();

        foreach (var stale in _panels.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            if (_panels.Remove(stale, out var stalePanel)) stalePanel.Cleanup();
        }
        foreach (var provider in providers)
        {
            if (!_panels.TryGetValue(provider.Id, out var panel))
            {
                panel = new DashboardProviderPanel { VerticalAlignment = VerticalAlignment.Top };
                _panels[provider.Id] = panel;
            }
            if (panel.CurrentData != null && DashboardContent.ProviderPanelStaticEquals(panel.CurrentData, provider))
                panel.ApplyClockTick(provider);
            else
                panel.Data = provider;
        }

        EmptyPanel.Visibility = providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Arrange(force: false);
    }

    private void OnScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            ArrangeSummary();
            Arrange(force: false);
            ArrangeGraphs();
        }
        catch (Exception ex) { App.LogHandled("Dashboard layout update failed", ex); }
    }

    /// <summary>One toggle per provider in the section header, one chart card per provider whose graphs are on.</summary>
    private void UpdateGraphs(IReadOnlyList<DashboardProvider> providers)
    {
        var ids = providers.Select(p => p.Id).ToList();
        foreach (var stale in _graphCards.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            if (_graphCards.Remove(stale, out var staleCard)) staleCard.Cleanup();
        }
        foreach (var stale in _graphToggles.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            if (_graphToggles.Remove(stale, out var staleToggle)) staleToggle.Click -= OnGraphToggleClicked;
        }

        foreach (var provider in providers)
        {
            if (!_graphToggles.TryGetValue(provider.Id, out var toggle))
            {
                // A check box per provider, not an accent-filled toggle button: the name stays in primary text on the
                // page background in both themes, only the small box takes the accent colour, and on or off reads from
                // the check mark rather than from colour. Text centred on the 32 DIP box row so nothing is clipped.
                toggle = new CheckBox
                {
                    Tag = provider.Id, MinWidth = 0, FontSize = 13, Padding = new Thickness(8, 0, 0, 0),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                toggle.Click += OnGraphToggleClicked;
                _graphToggles[provider.Id] = toggle;
            }
            var shown = !_hiddenGraphs.Contains(provider.Id);
            if (!string.Equals(toggle.Content as string, provider.DisplayName, StringComparison.Ordinal) || toggle.IsChecked != shown)
            {
                toggle.Content = provider.DisplayName;
                if (toggle.IsChecked != shown) toggle.IsChecked = shown;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, $"{provider.DisplayName} graphs");
                ToolTipService.SetToolTip(toggle, shown ? $"Hide {provider.DisplayName} graphs" : $"Show {provider.DisplayName} graphs");
            }

            if (shown)
            {
                if (!_graphCards.TryGetValue(provider.Id, out var card))
                {
                    card = new DashboardGraphCard { VerticalAlignment = VerticalAlignment.Top };
                    _graphCards[provider.Id] = card;
                }
                if (!DashboardContent.GraphProviderEquals(card.Data, provider)) card.Data = provider;
            }
            else if (_graphCards.Remove(provider.Id, out var hiddenCard))
            {
                hiddenCard.Cleanup();
            }
        }

        var wanted = providers.Select(p => _graphToggles[p.Id]).ToList();
        if (!GraphToggles.Children.SequenceEqual(wanted))
        {
            GraphToggles.Children.Clear();
            foreach (var toggle in wanted) GraphToggles.Children.Add(toggle);
        }

        GraphsSection.Visibility = providers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GraphsHiddenNote.Visibility = providers.Count > 0 && _graphCards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ArrangeGraphs(providers);
    }

    private void ArrangeGraphs(IReadOnlyList<DashboardProvider>? providers = null)
    {
        providers ??= _snapshot?.Providers;
        if (providers == null) return;
        var order = providers.Where(p => _graphCards.ContainsKey(p.Id)).Select(p => p.Id).ToList();
        var available = Math.Max(0, MainScroll.ActualWidth - 12);
        // Sized by all providers, not just the visible ones, so one card left on does not stretch across the window
        var columns = Math.Clamp((int)Math.Floor((available + ColumnGap) / (MinPanelWidth + ColumnGap)), 1, Math.Max(1, providers.Count));
        var key = $"{columns}|{string.Join('|', order)}";
        if (key == _graphsArranged) return;
        _graphsArranged = key;

        foreach (var card in _graphCards.Values) if (card.Parent is Panel parent) parent.Children.Remove(card);
        GraphsGrid.Children.Clear();
        GraphsGrid.ColumnDefinitions.Clear();
        GraphsGrid.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++) GraphsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < (order.Count + columns - 1) / columns; r++) GraphsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < order.Count; i++)
        {
            var card = _graphCards[order[i]];
            Grid.SetColumn(card, i % columns);
            Grid.SetRow(card, i / columns);
            GraphsGrid.Children.Add(card);
        }
    }

    private void OnGraphToggleClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not ToggleButton { Tag: string id } toggle) return;
            var visible = toggle.IsChecked == true;
            if (visible) _hiddenGraphs.Remove(id); else _hiddenGraphs.Add(id);
            if (_snapshot != null) UpdateGraphs(_snapshot.Providers);
            GraphVisibilityChanged?.Invoke(this, new GraphVisibilityChange(id, visible));
        }
        catch (Exception ex) { App.LogHandled("Dashboard graphs toggle failed", ex); }
    }

    private void OnAttentionClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: string id } && _panels.TryGetValue(id, out var panel))
                panel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0, VerticalOffset = -8 });
        }
        catch (Exception ex) { App.LogHandled("Dashboard scroll to provider failed", ex); }
    }

    /// <summary>
    /// Lays the provider panels out side by side in as many columns as fit (each at least <see cref="MinPanelWidth"/> wide),
    /// so the tops of the panels form the comparison row. With fewer columns than panels each panel goes into the
    /// currently shortest column. Opening a panel's details never re-arranges (nothing jumps under the pointer).
    /// </summary>
    private void Arrange(bool force)
    {
        var available = Math.Max(0, MainScroll.ActualWidth - 12);
        var order = _panels.Keys.ToList();
        var count = Math.Max(1, order.Count);
        var columns = Math.Clamp((int)Math.Floor((available + ColumnGap) / (MinPanelWidth + ColumnGap)), 1, count);
        var orderKey = string.Join('|', order);
        if (!force && columns == _arrangedColumns && orderKey == _arrangedOrder) return;

        _arrangedColumns = columns;
        _arrangedOrder = orderKey;
        // Release the panels from their previous column before re-parenting them
        foreach (var stack in _columnStacks) stack.Children.Clear();
        _columnStacks.Clear();
        ProvidersGrid.Children.Clear();
        ProvidersGrid.ColumnDefinitions.Clear();

        var heights = new double[columns];
        for (var c = 0; c < columns; c++)
        {
            ProvidersGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var stack = new StackPanel { Spacing = ColumnGap, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(stack, c);
            _columnStacks.Add(stack);
            ProvidersGrid.Children.Add(stack);
        }
        foreach (var id in order)
        {
            var panel = _panels[id];
            var shortest = Array.IndexOf(heights, heights.Min());
            _columnStacks[shortest].Children.Add(panel);
            heights[shortest] += EstimateHeight(panel.CurrentData) + ColumnGap;
        }
    }

    /// <summary>A rough collapsed height for balancing columns (real heights aren't known before layout).</summary>
    private static double EstimateHeight(DashboardProvider? provider) =>
        provider == null ? 0
            : 90 + provider.Windows.Count * 64
              + (provider.AuthRequired ? 90 : string.IsNullOrWhiteSpace(provider.Error) ? 0 : 110)
              + (DashboardFormat.HasIncident(provider) ? 130 : 0)
              + (provider.Credits != null ? 30 : 0)
              + (provider.Cost != null ? 250 : 0)
              + ApiSectionHeight(provider.ApiCredits)
              + GeminiApiSectionHeight(provider.GeminiApi);

    /// <summary>The Claude API credits section: about 150 plus a row per rendered contributor when Ok, a line otherwise, nothing without a key.</summary>
    private static double ApiSectionHeight(ApiCreditsInfo? api) => api == null ? 0
        : api.State == ApiCreditsState.NoKey ? 0
        : api.State != ApiCreditsState.Ok ? 70
        : 150 + 28 * DashboardProviderPanel.ApiUseRowCount(api);

    /// <summary>The Gemini API spend section: nothing without a log, a line when unreadable, else about 130 plus a row per model and a line per route.</summary>
    private static double GeminiApiSectionHeight(GeminiApiSpendInfo? gemini) => gemini == null ? 0
        : gemini.State == GeminiApiSpendState.NoLog ? 0
        : gemini.State != GeminiApiSpendState.Ok ? 70
        : 130 + 28 * DashboardProviderPanel.GeminiUseRowCount(gemini) + 22 * DashboardProviderPanel.GeminiRouteLineCount(gemini);

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    private void OnSettingsClicked(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void OnTopmostClicked(object sender, RoutedEventArgs e) => TopmostChanged?.Invoke(this, TopmostToggle.IsChecked == true);
    private void OnFullScreenClicked(object sender, RoutedEventArgs e) => FullScreenChanged?.Invoke(this, FullScreenToggle.IsChecked == true);
}
