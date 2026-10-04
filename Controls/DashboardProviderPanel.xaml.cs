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

    public DashboardProviderPanel()
    {
        InitializeComponent();
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((DashboardProviderPanel)d).Refresh();

    private void Refresh()
    {
        var data = Data;
        BuildCostRows(data?.Cost);

        CreditsText.Text = data?.Credits is { } credits
            ? $"{credits.Used:0.##} of {credits.Limit:0.##} used · {credits.Remaining:0.##} remaining"
            : string.Empty;

        HistoryText.Text = data is { HistorySampleCount: > 0 }
            ? $"Usage history: {DashboardFormat.Plural(data.HistorySampleCount, "sample")} since {DashboardFormat.LocalDate(data.HistoryOldestUtc)}"
            : "Usage history: no samples yet";

        Bindings.Update();
    }

    private void BuildCostRows(DashboardCost? cost)
    {
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
        TokenRows = tokens.Select(t => new DashboardBarRow(t.Label,
            $"{CostFormatter.Tokens(t.Count)} · {t.Count * 100.0 / tokenTotal:0.#}%", t.Count * 100.0 / tokenTotal)).ToList();

        var maxCost = cost.TopModels.Max(m => m.Cost ?? 0m, 0m);
        var maxTokens = Math.Max(1, cost.TopModels.Select(m => m.TotalTokens).DefaultIfEmpty(1).Max());
        ModelRows = cost.TopModels.Select(m => new DashboardBarRow(m.ModelName,
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
        CostFacts = facts;
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
