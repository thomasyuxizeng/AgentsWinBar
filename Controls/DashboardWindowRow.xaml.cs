using CodexBarWin.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexBarWin.Controls;

/// <summary>One usage window on a dashboard provider panel. Binds to a <see cref="DashboardWindow"/>.</summary>
public sealed partial class DashboardWindowRow : UserControl
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data),
        typeof(DashboardWindow), typeof(DashboardWindowRow), new PropertyMetadata(null, OnDataChanged));

    public DashboardWindow? Data
    {
        get => (DashboardWindow?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public DashboardWindowRow()
    {
        InitializeComponent();
    }

    public void RefreshTimes() => Bindings.Update();

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue != null) ((DashboardWindowRow)d).Bindings.Update();
    }
}
