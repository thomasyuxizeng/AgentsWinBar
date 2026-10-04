using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexBarWin.Controls;

/// <summary>A label, a value and a 0-100 bar (token mix, top models).</summary>
public sealed record DashboardBarRow(string Label, string Value, double Percent);

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
    public int WindowCount => Data?.Windows.Count ?? 0;

    private const double SplitMinWidth = 580;

    private readonly Dictionary<string, DashboardWindowRow> _rows = new();
    private string _windowsLayout = string.Empty;
    private bool _stacked;

    public DashboardProviderPanel()
    {
        InitializeComponent();
    }

    private void OnWindowsHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            // Two columns need room; a narrow panel stacks them
            var stacked = e.NewSize.Width > 0 && e.NewSize.Width < SplitMinWidth;
            if (stacked == _stacked) return;
            _stacked = stacked;
            BuildWindows(Data);
        }
        catch (Exception ex) { App.LogHandled("Dashboard window layout update failed", ex); }
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DashboardProviderPanel)d).Refresh();

    public void RefreshTimes()
    {
        Bindings.Update();
        foreach (var row in _rows.Values) row.RefreshTimes();
        DashboardTemplateBindings.Update(CostSection, item => item is DashboardFact { Label: "Scanned" } fact && Data?.Cost is { } cost
            ? fact with { Value = $"{DashboardFormat.Local(cost.CapturedAtUtc)} · {DashboardFormat.Ago(cost.CapturedAtUtc)}" } : item);
    }

    private void Refresh()
    {
        var data = Data;
        if (Content is UIElement content) content.Visibility = data == null ? Visibility.Collapsed : Visibility.Visible;
        BuildWindows(data);
        BuildCostRows(data?.Cost);

        CreditsText.Text = data?.Credits is { } credits
            ? $"{credits.Used:0.##} of {credits.Limit:0.##} used · {credits.Remaining:0.##} remaining"
            : string.Empty;

        HistoryText.Text = data is { HistorySampleCount: > 0 }
            ? $"Usage history: {DashboardFormat.Plural(data.HistorySampleCount, "sample")} since {DashboardFormat.LocalDate(data.HistoryOldestUtc)}"
            : "Usage history: no samples yet";

        Bindings.Update();
    }

    /// <summary>
    /// Lays the usage windows out. A provider with an External pool (Gemini through Antigravity) gets two columns,
    /// its own models on the left and the External windows on the right, paired row by row so the bars line up;
    /// every other provider lists its windows in one column.
    /// </summary>
    private void BuildWindows(DashboardProvider? data)
    {
        var windows = data?.Windows ?? [];
        var split = !_stacked && windows.Any(w => w.Kind == DashboardWindowKind.Extra && w.Accent == WindowAccent.ClaudeAndOpenAi);
        var left = split ? windows.Where(w => w.Kind != DashboardWindowKind.Extra).ToList() : windows.ToList();
        var right = split ? windows.Where(w => w.Kind == DashboardWindowKind.Extra).ToList() : [];

        foreach (var window in windows)
        {
            if (!_rows.TryGetValue(window.Key, out var row))
            {
                row = new DashboardWindowRow { VerticalAlignment = VerticalAlignment.Top };
                _rows[window.Key] = row;
            }
            if (!DashboardContent.WindowEquals(row.Data, window)) row.Data = window;
            else row.RefreshTimes();
        }

        var layout = $"{split}|{string.Join(',', left.Select(w => $"{w.Key.Length}:{w.Key}"))}|{string.Join(',', right.Select(w => $"{w.Key.Length}:{w.Key}"))}|{data?.DisplayName}";
        if (layout == _windowsLayout) return;
        _windowsLayout = layout;

        foreach (var stale in _rows.Keys.Where(k => windows.All(w => w.Key != k)).ToList()) _rows.Remove(stale);
        WindowsHost.Children.Clear();
        WindowsHost.ColumnDefinitions.Clear();
        WindowsHost.RowDefinitions.Clear();
        if (windows.Count == 0) return;

        var columns = split ? 2 : 1;
        for (var c = 0; c < columns; c++) WindowsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var caption = (Style)Resources["SectionCaption"];
        var rows = Math.Max(left.Count, right.Count);
        for (var r = 0; r < rows + 1; r++) WindowsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Place(UIElement element, int row, int column)
        {
            Grid.SetRow((FrameworkElement)element, row);
            Grid.SetColumn((FrameworkElement)element, column);
            WindowsHost.Children.Add(element);
        }

        // The caption sits closer to its first row than the rows are to each other
        Place(new TextBlock { Text = split ? $"{data!.DisplayName.ToUpperInvariant()} MODELS" : "USAGE WINDOWS", Style = caption, Margin = new Thickness(0, 0, 0, -8) }, 0, 0);
        if (split) Place(new TextBlock { Text = "EXTERNAL · CLAUDE AND GPT", Style = caption, Margin = new Thickness(0, 0, 0, -8) }, 0, 1);
        for (var i = 0; i < left.Count; i++) Place(_rows[left[i].Key], i + 1, 0);
        for (var i = 0; i < right.Count; i++) Place(_rows[right[i].Key], i + 1, 1);
    }

    private DashboardCost? _costRowsData;
    private bool _costRowsBuilt;

    private void BuildCostRows(DashboardCost? cost)
    {
        if (_costRowsBuilt && DashboardContent.CostEquals(_costRowsData, cost)) return;
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
