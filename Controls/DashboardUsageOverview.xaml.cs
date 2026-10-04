using CodexBarWin.Helpers;
using CodexBarWin.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexBarWin.Controls;

/// <summary>
/// The dashboard's overview: one compact card per provider with a one-line bar for every usage window, so all
/// the limits are visible at once on load. Cards share the width in as many columns as fit.
/// </summary>
public sealed partial class DashboardUsageOverview : UserControl
{
    private const double MinCardWidth = 380;
    private const double Gap = 14;

    private readonly Dictionary<string, DashboardUsageCard> _cards = new();
    private List<string> _order = [];
    private int _columns = -1;
    private string _arranged = string.Empty;

    public DashboardUsageOverview()
    {
        InitializeComponent();
    }

    public void Update(IReadOnlyList<DashboardProvider> providers)
    {
        foreach (var stale in _cards.Keys.Where(k => providers.All(p => p.Id != k)).ToList()) _cards.Remove(stale);
        foreach (var provider in providers)
        {
            if (!_cards.TryGetValue(provider.Id, out var card)) _cards[provider.Id] = card = new DashboardUsageCard();
            if (!DashboardContent.ProviderEquals(card.Data, provider)) card.Data = provider;
            else card.RefreshTimes();
        }
        _order = providers.Select(p => p.Id).ToList();
        Arrange();
    }

    public void RefreshTimes()
    {
        foreach (var card in _cards.Values) card.RefreshTimes();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try { Arrange(); }
        catch (Exception ex) { App.LogHandled("Dashboard layout update failed", ex); }
    }

    private void Arrange()
    {
        var count = Math.Max(1, _order.Count);
        var columns = Math.Clamp((int)Math.Floor((CardsGrid.ActualWidth + Gap) / (MinCardWidth + Gap)), 1, count);
        var key = string.Join('|', _order);
        if (columns == _columns && key == _arranged) return;
        _columns = columns;
        _arranged = key;

        CardsGrid.Children.Clear();
        CardsGrid.ColumnDefinitions.Clear();
        CardsGrid.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++) CardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < (int)Math.Ceiling(_order.Count / (double)columns); r++) CardsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < _order.Count; i++)
        {
            var card = _cards[_order[i]];
            Grid.SetRow(card, i / columns);
            Grid.SetColumn(card, i % columns);
            CardsGrid.Children.Add(card);
        }
    }
}
