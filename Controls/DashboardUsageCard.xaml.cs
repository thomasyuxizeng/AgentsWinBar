using CodexBarWin.Helpers;
using CodexBarWin.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexBarWin.Controls;

/// <summary>One provider's compact usage card for the dashboard overview.</summary>
public sealed partial class DashboardUsageCard : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data),
        typeof(DashboardProvider), typeof(DashboardUsageCard), new PropertyMetadata(null, OnDataChanged));

    private readonly Dictionary<string, DashboardMiniBar> _rows = new();
    private string _layout = string.Empty;

    public DashboardProvider? Data
    {
        get => (DashboardProvider?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public DashboardUsageCard()
    {
        InitializeComponent();
    }

    public void RefreshTimes()
    {
        Bindings.Update();
        foreach (var row in _rows.Values) row.RefreshTimes();
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (DashboardUsageCard)d;
        card.BuildRows(card.Data);
        if (card.Data != null) card.Bindings.Update();
    }

    private void BuildRows(DashboardProvider? data)
    {
        var windows = data?.Windows ?? [];
        foreach (var window in windows)
        {
            if (!_rows.TryGetValue(window.Key, out var row)) _rows[window.Key] = row = new DashboardMiniBar();
            if (!DashboardContent.WindowEquals(row.Data, window)) row.Data = window;
            else row.RefreshTimes();
        }

        var layout = string.Join(',', windows.Select(w => w.Key));
        if (layout == _layout) return;
        _layout = layout;

        foreach (var stale in _rows.Keys.Where(k => windows.All(w => w.Key != k)).ToList()) _rows.Remove(stale);
        RowsHost.Children.Clear();
        // A little air before the External pool, so Gemini's own models and the pool read as two groups
        var external = windows.Any(w => w.Kind == DashboardWindowKind.Extra && w.Accent == WindowAccent.ClaudeAndOpenAi);
        var firstExtraSeen = false;
        foreach (var window in windows)
        {
            var row = _rows[window.Key];
            var startsGroup = external && window.Kind == DashboardWindowKind.Extra && !firstExtraSeen;
            if (window.Kind == DashboardWindowKind.Extra) firstExtraSeen = true;
            row.Margin = new Thickness(0, startsGroup ? 6 : 0, 0, 0);
            RowsHost.Children.Add(row);
        }
    }
}
