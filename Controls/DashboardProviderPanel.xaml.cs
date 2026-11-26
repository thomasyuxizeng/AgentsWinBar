using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using AgentsWinBar.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentsWinBar.Controls;

/// <summary>A label, a value and a 0-100 bar (token mix, top models).</summary>
public sealed record DashboardBarRow(string Label, string Value, double Percent);

/// <summary>One team member's use of the API credits pool: name, amount, share text and the share as a 0-100 bar. <paramref name="IsOthers"/> marks the folded "N others" row.</summary>
public sealed record DashboardApiUseRow(string Label, string Amount, string Share, double Percent, bool IsYou, bool IsOthers = false, string OfWhat = "of the pool")
{
    public string AutomationName => $"{Label}, {Amount}, {Share} {OfWhat}";
}

/// <summary>A label and a value line (cost facts, diagnostics).</summary>
public sealed record DashboardFact(string Label, string Value);

/// <summary>
/// One provider's complete dashboard card. Binds to a <see cref="DashboardProvider"/>; the derived
/// presentation rows (token mix, model bars, fact lines) are built here, nothing is recomputed from raw data.
/// </summary>
public sealed partial class DashboardProviderPanel : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data),
        typeof(DashboardProvider), typeof(DashboardProviderPanel), new PropertyMetadata(null, OnDataChanged));

    public DashboardProvider? Data
    {
        get => (DashboardProvider?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IReadOnlyList<DashboardBarRow> TokenRows { get; private set; } = [];
    public IReadOnlyList<DashboardBarRow> ModelRows { get; private set; } = [];
    public IReadOnlyList<DashboardFact> CostFacts { get; private set; } = [];
    public IReadOnlyList<DashboardApiUseRow> ApiUseRows { get; private set; } = [];
    public IReadOnlyList<DashboardApiUseRow> GeminiUseRows { get; private set; } = [];
    public int GeminiUseRowTotal => GeminiUseRows.Count;
    public int WindowCount => Data?.Windows.Count ?? 0;

    /// <summary>Pace projections, window facts, cost breakdowns and the history footer.</summary>
    public bool ShowDetails { get; private set; }

    private const double SplitMinWidth = 580;
    private const double RowGap = 16;
    private const double GroupedRowGap = 10;

    /// <summary>The details state per provider, kept while the app runs so reopening the dashboard keeps it.</summary>
    private static readonly Dictionary<string, bool> DetailsByProvider = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, DashboardWindowRow> _rows = new();
    private string _windowsLayout = string.Empty;
    private bool _stacked;
    private bool _disposed;
    private bool _suppressFullRefresh;
    private string? _iconProviderId;
    private string? _statusColorHex;
    private string? _freshnessTip;
    private string? _statusHeaderTip;
    private string? _apiUpdatedTip;
    private string? _lastScannedText;
    private DashboardProvider? _currentData;

    public DashboardProvider? CurrentData => _currentData ?? Data;

    public DashboardProviderPanel()
    {
        InitializeComponent();
        UpdateDetailsButton();
    }

    /// <summary>Closes live InfoBars, stops child window rows/meters, detaches event handlers and compiled bindings.</summary>
    public void Cleanup()
    {
        if (_disposed) return;
        _disposed = true;
        WindowsHost.SizeChanged -= OnWindowsHostSizeChanged;
        DetailsButton.Click -= OnDetailsClicked;
        SignInButton.Click -= OnSignInClicked;
        ApiOpenSettingsButton.Click -= OnOpenApiSettingsClicked;

        SignedOutBar.IsOpen = false;
        SignedOutBar.Visibility = Visibility.Collapsed;
        SignedOutBar.ActionButton = null;
        SignedOutBar.Content = null;
        IncidentBar.IsOpen = false;
        IncidentBar.Visibility = Visibility.Collapsed;
        IncidentBar.Content = null;
        FetchErrorBar.IsOpen = false;
        FetchErrorBar.Visibility = Visibility.Collapsed;
        FetchErrorBar.Content = null;

        ApiMeter.Cleanup();
        foreach (var row in _rows.Values) row.Cleanup();
        _rows.Clear();
        WindowsHost.Children.Clear();
        WindowsHost.ColumnDefinitions.Clear();

        ApiUseList.ItemsSource = null;
        ApiUseList.ItemTemplate = null;
        GeminiUseList.ItemsSource = null;
        GeminiUseList.ItemTemplate = null;
        TokenMixList.ItemsSource = null;
        TokenMixList.ItemTemplate = null;
        TopModelsList.ItemsSource = null;
        TopModelsList.ItemTemplate = null;
        CostFactsList.ItemsSource = null;
        CostFactsList.ItemTemplate = null;
        TokenRows = ModelRows = [];
        CostFacts = [];
        ApiUseRows = GeminiUseRows = [];
        _costRowsData = null;
        _costRowsBuilt = false;
        ProviderIcon.Data = null;
        StatusDot.Fill = null;
        ToolTipService.SetToolTip(FreshnessGrid, null);
        ToolTipService.SetToolTip(StatusHeaderPanel, null);
        ToolTipService.SetToolTip(ApiOkPanel, null);
        _iconProviderId = null;
        _statusColorHex = null;
        _freshnessTip = null;
        _statusHeaderTip = null;
        _apiUpdatedTip = null;
        _lastScannedText = null;
        _currentData = null;

        Bindings?.StopTracking();
        SetValue(DataProperty, null);
        DataContext = null;
    }

    private void OnDetailsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowDetails = !ShowDetails;
            if (CurrentData?.Id is { Length: > 0 } id) DetailsByProvider[id] = ShowDetails;
            ApplyDetails();
        }
        catch (Exception ex) { App.LogHandled("Dashboard details toggle failed", ex); }
    }

    private void OnSignInClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CurrentData?.Id is { Length: > 0 } id && Models.ProviderConstants.IsValidProvider(id)) SignInAction.Start(id);
        }
        catch (Exception ex) { App.LogHandled("Dashboard sign-in failed", ex); }
    }

    /// <summary>No key or a rejected key: open Settings at the Anthropic API credits card.</summary>
    private void OnOpenApiSettingsClicked(object sender, RoutedEventArgs e)
    {
        try { SettingsWindow.OpenAtApiCredits(); }
        catch (Exception ex) { App.LogHandled("Opening Settings at the API credits card failed", ex); }
    }

    private void ApplyDetails()
    {
        if (_currentData != null && !ReferenceEquals(_currentData, Data))
        {
            _suppressFullRefresh = true;
            try
            {
                Data = _currentData;
            }
            finally
            {
                _suppressFullRefresh = false;
            }
        }
        var data = CurrentData;
        UpdateDetailsButton();
        BuildWindows(data);
        if (ShowDetails)
        {
            BuildCostRows(data?.Cost);
            var historyText = HistoryTextFor(data);
            if (!string.Equals(HistoryText.Text, historyText, StringComparison.Ordinal))
                HistoryText.Text = historyText;
        }
        Bindings.Update();
    }

    private void UpdateDetailsButton()
    {
        var name = CurrentData?.DisplayName is { Length: > 0 } provider ? $" for {provider}" : string.Empty;
        DetailsText.Text = ShowDetails ? "Hide details" : "Show details";
        DetailsGlyph.Glyph = ShowDetails ? "" : "";   // chevron up / down
        AutomationProperties.SetName(DetailsButton, (ShowDetails ? "Hide details" : "Show details") + name);
        ToolTipService.SetToolTip(DetailsButton, ShowDetails
            ? "Hide pace projections and breakdowns"
            : "Show pace projections and breakdowns");
    }

    private void OnWindowsHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            // Two columns need room; a narrow panel stacks them
            var stacked = e.NewSize.Width > 0 && e.NewSize.Width < SplitMinWidth;
            if (stacked == _stacked) return;
            _stacked = stacked;
            BuildWindows(CurrentData);
        }
        catch (Exception ex) { App.LogHandled("Dashboard window layout update failed", ex); }
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (DashboardProviderPanel)d;
        if (panel._disposed || panel._suppressFullRefresh) return;
        panel._currentData = panel.Data;
        panel.Refresh();
    }

    public void ApplyClockTick(DashboardProvider updated)
    {
        if (_disposed) return;
        _currentData = updated;
        RefreshTimesCore(updated, updated.Windows);
    }

    public void RefreshTimes()
    {
        if (_disposed || CurrentData is not { } data) return;
        RefreshTimesCore(data, null);
    }

    private static string HistoryTextFor(DashboardProvider? data) => data is { HistorySampleCount: > 0 }
        ? $"Usage history: {DashboardFormat.Plural(data.HistorySampleCount, "sample")} since {DashboardFormat.LocalDate(data.HistoryOldestUtc)}"
        : "Usage history: no samples yet";

    private void RefreshTimesCore(DashboardProvider data, IReadOnlyList<DashboardWindow>? updatedWindows)
    {
        var freshness = DashboardFormat.FreshnessLine(data);
        if (DashboardFormat.VisFresh(data) == Visibility.Visible &&
            !string.Equals(FreshnessFreshText.Text, freshness, StringComparison.Ordinal))
            FreshnessFreshText.Text = freshness;
        if (DashboardFormat.VisStale(data) == Visibility.Visible &&
            !string.Equals(FreshnessStaleText.Text, freshness, StringComparison.Ordinal))
            FreshnessStaleText.Text = freshness;

        var freshnessTip = DashboardFormat.FreshnessTip(data);
        if (!string.Equals(_freshnessTip, freshnessTip, StringComparison.Ordinal))
        {
            _freshnessTip = freshnessTip;
            ToolTipService.SetToolTip(FreshnessGrid, string.IsNullOrEmpty(freshnessTip) ? null : freshnessTip);
        }

        if (StatusHeaderPanel.Visibility == Visibility.Visible)
        {
            var statusTip = DashboardFormat.StatusHeaderTip(data);
            if (!string.Equals(_statusHeaderTip, statusTip, StringComparison.Ordinal))
            {
                _statusHeaderTip = statusTip;
                ToolTipService.SetToolTip(StatusHeaderPanel, string.IsNullOrEmpty(statusTip) ? null : statusTip);
            }
        }

        if (DashboardFormat.HasIncident(data))
        {
            var age = DashboardFormat.IncidentAge(data);
            var ageVis = DashboardFormat.VisText(age);
            if (IncidentAgeText.Visibility != ageVis) IncidentAgeText.Visibility = ageVis;
            if (!string.Equals(IncidentAgeText.Text, age, StringComparison.Ordinal))
                IncidentAgeText.Text = age;
        }

        if (DashboardFormat.HasFetchError(data))
        {
            var lastKnown = DashboardFormat.LastKnownData(data);
            var lastKnownVis = DashboardFormat.VisText(lastKnown);
            if (LastKnownDataText.Visibility != lastKnownVis) LastKnownDataText.Visibility = lastKnownVis;
            if (!string.Equals(LastKnownDataText.Text, lastKnown, StringComparison.Ordinal))
                LastKnownDataText.Text = lastKnown;
        }

        if (data.ApiCredits is { State: ApiCreditsState.Ok } api)
        {
            var apiUpdatedTip = DashboardFormat.ApiUpdatedTip(api);
            if (!string.Equals(_apiUpdatedTip, apiUpdatedTip, StringComparison.Ordinal))
            {
                _apiUpdatedTip = apiUpdatedTip;
                ToolTipService.SetToolTip(ApiOkPanel, string.IsNullOrEmpty(apiUpdatedTip) ? null : apiUpdatedTip);
            }
            var apiCaption = DashboardFormat.ApiCaption(api);
            if (!string.Equals(ApiCaptionText.Text, apiCaption, StringComparison.Ordinal))
                ApiCaptionText.Text = apiCaption;
            if (api.IsStale)
            {
                var apiStale = DashboardFormat.ApiStaleText(api);
                if (!string.Equals(ApiStaleTextBlock.Text, apiStale, StringComparison.Ordinal))
                    ApiStaleTextBlock.Text = apiStale;
            }
            var apiTime = DashboardFormat.ApiTime(api);
            if ((ApiMeter.TimePercent < 0 && apiTime >= 0) || Math.Abs(apiTime - ApiMeter.TimePercent) >= 0.05)
                ApiMeter.TimePercent = apiTime;
        }

        if (data.GeminiApi is { State: GeminiApiSpendState.Ok })
        {
            var geminiCaption = DashboardFormat.GeminiCaption(data.GeminiApi);
            if (!string.Equals(GeminiCaptionText.Text, geminiCaption, StringComparison.Ordinal))
                GeminiCaptionText.Text = geminiCaption;
        }

        if (data.Cost is { IsStale: true })
        {
            var costStale = DashboardFormat.CostStale(data.Cost);
            if (!string.Equals(CostStaleText.Text, costStale, StringComparison.Ordinal))
                CostStaleText.Text = costStale;
        }

        if (updatedWindows != null)
        {
            foreach (var window in updatedWindows)
            {
                if (_rows.TryGetValue(window.Key, out var row))
                    row.ApplyClockTick(window);
            }
        }
        else
        {
            foreach (var row in _rows.Values) row.RefreshTimes();
        }

        if (ShowDetails)
        {
            var historyText = HistoryTextFor(data);
            if (!string.Equals(HistoryText.Text, historyText, StringComparison.Ordinal))
                HistoryText.Text = historyText;
            if (data.Cost is { } cost)
            {
                var scanned = $"{DashboardFormat.Local(cost.CapturedAtUtc)} · {DashboardFormat.Ago(cost.CapturedAtUtc)}";
                if (!string.Equals(_lastScannedText, scanned, StringComparison.Ordinal))
                {
                    _lastScannedText = scanned;
                    DashboardTemplateBindings.Update(CostSection, item => item is DashboardFact { Label: "Scanned" } fact
                        ? fact with { Value = scanned } : item);
                }
            }
        }
    }

    private void Refresh()
    {
        var data = Data;
        if (Content is UIElement content) content.Visibility = data == null ? Visibility.Collapsed : Visibility.Visible;
        if (data != null && !string.Equals(_iconProviderId, data.Id, StringComparison.Ordinal))
        {
            _iconProviderId = data.Id;
            ProviderIcon.Data = DashboardFormat.Icon(data.Id);
        }
        var hex = ProviderStatusPresentation.ColorHex(data?.ServiceStatus);
        if (!string.Equals(_statusColorHex, hex, StringComparison.Ordinal))
        {
            _statusColorHex = hex;
            StatusDot.Fill = ProviderStatusVisuals.CreateBrush(data?.ServiceStatus);
        }

        var freshnessTip = DashboardFormat.FreshnessTip(data);
        if (!string.Equals(_freshnessTip, freshnessTip, StringComparison.Ordinal))
        {
            _freshnessTip = freshnessTip;
            ToolTipService.SetToolTip(FreshnessGrid, string.IsNullOrEmpty(freshnessTip) ? null : freshnessTip);
        }
        var statusTip = DashboardFormat.StatusHeaderTip(data);
        if (!string.Equals(_statusHeaderTip, statusTip, StringComparison.Ordinal))
        {
            _statusHeaderTip = statusTip;
            ToolTipService.SetToolTip(StatusHeaderPanel, string.IsNullOrEmpty(statusTip) ? null : statusTip);
        }
        var apiUpdatedTip = data?.ApiCredits is { State: ApiCreditsState.Ok } api ? DashboardFormat.ApiUpdatedTip(api) : null;
        if (!string.Equals(_apiUpdatedTip, apiUpdatedTip, StringComparison.Ordinal))
        {
            _apiUpdatedTip = apiUpdatedTip;
            ToolTipService.SetToolTip(ApiOkPanel, string.IsNullOrEmpty(apiUpdatedTip) ? null : apiUpdatedTip);
        }

        var details = data?.Id is { Length: > 0 } id && DetailsByProvider.TryGetValue(id, out var open) && open;
        ShowDetails = details;
        UpdateDetailsButton();
        BuildWindows(data);
        BuildCostRows(data?.Cost);
        BuildApiUseRows(data?.ApiCredits);
        BuildGeminiUseRows(data?.GeminiApi);

        var creditsText = data?.Credits is { } credits
            ? $"Credits: {credits.Used:0.##} of {credits.Limit:0.##} used · {credits.Remaining:0.##} remaining"
            : string.Empty;
        if (!string.Equals(CreditsText.Text, creditsText, StringComparison.Ordinal))
            CreditsText.Text = creditsText;

        var historyText = HistoryTextFor(data);
        if (!string.Equals(HistoryText.Text, historyText, StringComparison.Ordinal))
            HistoryText.Text = historyText;

        _lastScannedText = data?.Cost is { } c
            ? $"{DashboardFormat.Local(c.CapturedAtUtc)} · {DashboardFormat.Ago(c.CapturedAtUtc)}"
            : null;

        Bindings.Update();
    }

    /// <summary>
    /// Lays the usage windows out. A provider with an External pool (Gemini through Antigravity) gets two columns in a
    /// wide panel, its own models on the left and the External windows on the right; a narrow panel stacks them under a
    /// sub-heading. Consecutive windows that reset at the same moment sit closer together and share one reset caption.
    /// </summary>
    private void BuildWindows(DashboardProvider? data)
    {
        var windows = data?.Windows ?? [];
        var hasExternal = windows.Any(w => w.Kind == DashboardWindowKind.Extra && w.Accent == WindowAccent.ClaudeAndOpenAi);
        var split = !_stacked && hasExternal;
        var own = hasExternal ? windows.Where(w => w.Kind != DashboardWindowKind.Extra).ToList() : windows.ToList();
        var external = hasExternal ? windows.Where(w => w.Kind == DashboardWindowKind.Extra).ToList() : [];

        foreach (var window in windows)
        {
            if (!_rows.TryGetValue(window.Key, out var row))
            {
                row = new DashboardWindowRow { VerticalAlignment = VerticalAlignment.Top };
                _rows[window.Key] = row;
            }
            if (row.CurrentData != null && DashboardContent.WindowPanelStaticEquals(row.CurrentData, window))
                row.ApplyClockTick(window);
            else
                row.Data = window;
        }

        // With the details open each window carries its own facts and chart, so each also keeps its own reset line
        var ownGroups = ShowDetails ? Enumerable.Range(0, own.Count).ToList() : DashboardGlance.ResetGroups(own);
        var externalGroups = ShowDetails ? Enumerable.Range(0, external.Count).ToList() : DashboardGlance.ResetGroups(external);

        // Rows are only re-parented when the set of windows or the column split changes. Regrouping (one of two
        // co-resetting windows resets first) and the details toggle are applied in place, so a row's meter is never
        // unloaded in the middle of its reset animation.
        var layout = $"{split}|{Keys(own)}|{Keys(external)}|{data?.DisplayName}";
        if (layout != _windowsLayout)
        {
            _windowsLayout = layout;
            Rebuild();
        }
        ApplyGroups(own, ownGroups, split ? 8 : 0);
        ApplyGroups(external, externalGroups, 8);

        void ApplyGroups(IReadOnlyList<DashboardWindow> list, IReadOnlyList<int> groups, double firstTop)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var row = _rows[list[i].Key];
                var size = groups.Count(g => g == groups[i]);
                var lastInGroup = i == list.Count - 1 || groups[i + 1] != groups[i];
                var grouped = i > 0 && groups[i - 1] == groups[i];
                var margin = new Thickness(0, i == 0 ? firstTop : grouped ? GroupedRowGap : RowGap, 0, 0);
                if (row.Margin != margin) row.Margin = margin;
                row.SetLayout(lastInGroup, size, ShowDetails);
            }
        }

        void Rebuild()
        {
            foreach (var stale in _rows.Keys.Where(k => windows.All(w => w.Key != k)).ToList())
            {
                if (_rows.Remove(stale, out var staleRow)) staleRow.Cleanup();
            }
            foreach (var row in _rows.Values) if (row.Parent is Panel parent) parent.Children.Remove(row);
            WindowsHost.Children.Clear();
            WindowsHost.ColumnDefinitions.Clear();
            if (windows.Count == 0) return;

            var columns = split ? 2 : 1;
            for (var c = 0; c < columns; c++) WindowsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var subHeading = (Style)Resources["SubHeading"];

            StackPanel Column(int column)
            {
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
                Grid.SetColumn(stack, column);
                WindowsHost.Children.Add(stack);
                return stack;
            }
            void AddRows(StackPanel stack, IEnumerable<DashboardWindow> list) { foreach (var w in list) stack.Children.Add(_rows[w.Key]); }
            TextBlock Heading(string text, double top) => new() { Text = text, Style = subHeading, Margin = new Thickness(0, top, 0, 0) };

            var left = Column(0);
            if (split)
            {
                left.Children.Add(Heading($"{data!.DisplayName} models", 0));
                AddRows(left, own);
                var right = Column(1);
                right.Children.Add(Heading("External · Claude and GPT", 0));
                AddRows(right, external);
            }
            else
            {
                AddRows(left, own);
                if (external.Count > 0)
                {
                    left.Children.Add(Heading("External · Claude and GPT", RowGap + 4));
                    AddRows(left, external);
                }
            }
        }

        static string Keys(IEnumerable<DashboardWindow> list) => string.Join(',', list.Select(w => $"{w.Key.Length}:{w.Key}"));
    }

    private const int MaxApiUseRows = 5;
    private const decimal MinApiUseSpend = 0.005m;

    /// <summary>
    /// One row per contributor in the order given (You first); bars are each member's share of the pool, so they add up to the main bar.
    /// Zero-spend teammates are dropped, at most <see cref="MaxApiUseRows"/> rows show and the rest fold into one "N others" row.
    /// </summary>
    private void BuildApiUseRows(ApiCreditsInfo? api)
    {
        var rows = ApiUseRowsFor(api);
        if (!ApiUseRows.SequenceEqual(rows)) ApiUseRows = rows;
    }

    /// <summary>How many "Used by" rows <paramref name="api"/> renders (for height estimates).</summary>
    internal static int ApiUseRowCount(ApiCreditsInfo? api) => ApiUseRowsFor(api).Count;

    private static List<DashboardApiUseRow> ApiUseRowsFor(ApiCreditsInfo? api)
    {
        if (api is not { State: ApiCreditsState.Ok, HasBreakdown: true }) return [];
        DashboardApiUseRow Row(string label, decimal spent, bool isYou, bool isOthers = false) => new(label,
            DashboardFormat.Money(spent, "USD"), DashboardFormat.ApiShare(spent, api.MonthlyUsd),
            api.MonthlyUsd > 0 ? Math.Clamp((double)(spent / api.MonthlyUsd) * 100, 0, 100) : 0, isYou, isOthers);

        var shown = api.Contributors.Where(c => c.IsYou || c.SpentUsd >= MinApiUseSpend).ToList();
        var rows = shown.Take(MaxApiUseRows).Select(c => Row(c.IsYou ? "You" : c.Name, c.SpentUsd, c.IsYou)).ToList();
        var rest = shown.Skip(MaxApiUseRows).ToList();
        if (rest.Count > 0) rows.Add(Row(rest.Count == 1 ? "1 other" : $"{rest.Count} others", rest.Sum(c => c.SpentUsd), false, isOthers: true));
        return rows;
    }

    /// <summary>"By model" rows for the Gemini API section; reassigned only when the sequence changed so the list does not flicker.</summary>
    private void BuildGeminiUseRows(GeminiApiSpendInfo? gemini)
    {
        var rows = GeminiUseRowsFor(gemini);
        if (!GeminiUseRows.SequenceEqual(rows)) GeminiUseRows = rows;
    }

    /// <summary>How many "By model" rows <paramref name="gemini"/> renders (for height estimates).</summary>
    internal static int GeminiUseRowCount(GeminiApiSpendInfo? gemini) => GeminiUseRowsFor(gemini).Count;

    /// <summary>How many route lines (AI Studio key, Vertex, Route not recorded) <paramref name="gemini"/> shows.</summary>
    internal static int GeminiRouteLineCount(GeminiApiSpendInfo? gemini) => gemini is not { State: GeminiApiSpendState.Ok } ? 0
        : new[] { gemini.AiStudioUsd, gemini.VertexUsd, gemini.UnrecordedRouteUsd }.Count(usd => usd >= DashboardFormat.GeminiMinShown);

    /// <summary>
    /// One row per model, largest first as given; bars are each model's share of the month's total. Zero-dollar models are
    /// dropped, at most <see cref="MaxApiUseRows"/> rows show and the rest fold into one "N others" row.
    /// </summary>
    private static List<DashboardApiUseRow> GeminiUseRowsFor(GeminiApiSpendInfo? gemini)
    {
        if (gemini is not { State: GeminiApiSpendState.Ok } || gemini.TotalUsd <= 0) return [];
        DashboardApiUseRow Row(string label, decimal usd, bool isOthers = false) => new(label,
            DashboardFormat.Money(usd, "USD"), DashboardFormat.ApiShare(usd, gemini.TotalUsd),
            Math.Clamp((double)(usd / gemini.TotalUsd) * 100, 0, 100), false, isOthers, "of this month's spend");

        var shown = gemini.Models.Where(m => m.Usd >= DashboardFormat.GeminiMinShown).ToList();
        var rows = shown.Take(MaxApiUseRows).Select(m => Row(DashboardFormat.GeminiModelLabel(m.Model), m.Usd)).ToList();
        var rest = shown.Skip(MaxApiUseRows).ToList();
        if (rest.Count > 0) rows.Add(Row(rest.Count == 1 ? "1 other" : $"{rest.Count} others", rest.Sum(m => m.Usd), isOthers: true));
        return rows;
    }

    private DashboardCost? _costRowsData;
    private bool _costRowsBuilt;

    private void BuildCostRows(DashboardCost? cost)
    {
        if (!ShowDetails) return;
        if (_costRowsBuilt && DashboardContent.CostContentEquals(_costRowsData, cost)) return;
        _costRowsBuilt = true;
        _costRowsData = cost;
        if (cost == null)
        {
            TokenRows = ModelRows = [];
            CostFacts = [];
            return;
        }

        var tokens = new (string Label, long Count)[]
        {
            ("Input", cost.InputTokens),
            ("Output", cost.OutputTokens),
            ("Cache read", cost.CacheReadTokens),
            ("Cache write", cost.CacheCreationTokens),
            ("Reasoning", cost.ReasoningTokens)
        }.Where(t => t.Count > 0).ToList();
        var tokenTotal = Math.Max(1, tokens.Sum(t => t.Count));
        var tokenRows = tokens.Select(t => new DashboardBarRow(t.Label,
            $"{CostFormatter.Tokens(t.Count)} · {t.Count * 100.0 / tokenTotal:0.#}%", t.Count * 100.0 / tokenTotal)).ToList();

        var maxCost = cost.TopModels.Max(m => m.Cost ?? 0m, 0m);
        var maxTokens = Math.Max(1, cost.TopModels.Select(m => m.TotalTokens).DefaultIfEmpty(1).Max());
        var modelRows = cost.TopModels.Select(m => new DashboardBarRow(m.ModelName,
            $"{(m.Cost.HasValue ? CostFormatter.Money(m.Cost.Value, cost.Currency) : "Unpriced")} · {CostFormatter.Tokens(m.TotalTokens)}",
            maxCost > 0 && m.Cost.HasValue ? (double)(m.Cost.Value / maxCost) * 100 : m.TotalTokens * 100.0 / maxTokens)).ToList();

        var coverage = cost.Coverage;
        var facts = new List<DashboardFact>
        {
            new("Average per day", cost.AverageDailyCost.HasValue
                ? CostFormatter.Money(cost.AverageDailyCost.Value, cost.Currency) + " (days with usage)" : "—"),
            new("Busiest day", cost.PeakDay is { } peak
                ? $"{peak.Date:ddd d MMM} · {CostFormatter.Money(peak.TotalCost, cost.Currency)} · {CostFormatter.Tokens(peak.TotalTokens)} tokens" : "—"),
            new("How it's priced", ProvenanceText(cost.Provenance)),
            new("Coverage", $"{coverage.Priced} priced · {coverage.Unpriced} unpriced · {coverage.Estimated} estimated · {coverage.Unmetered} unmetered"),
            new("Scanned", $"{DashboardFormat.Local(cost.CapturedAtUtc)} · {DashboardFormat.Ago(cost.CapturedAtUtc)}")
        };
        if (!TokenRows.SequenceEqual(tokenRows)) TokenRows = tokenRows;
        if (!ModelRows.SequenceEqual(modelRows)) ModelRows = modelRows;
        if (!CostFacts.SequenceEqual(facts)) CostFacts = facts;
    }

    private static string ProvenanceText(string? provenance) => provenance switch
    {
        "listPriceEstimate" => "List-price estimate (not your bill)",
        null or "" => "Unknown",
        _ => provenance
    };
}

internal static class DashboardLinq
{
    public static decimal Max<T>(this IEnumerable<T> source, Func<T, decimal> selector, decimal fallback) =>
        source.Select(selector).DefaultIfEmpty(fallback).Max();
}
