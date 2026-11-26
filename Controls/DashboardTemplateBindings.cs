using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace AgentsWinBar.Controls;

/// <summary>Re-evaluates compiled item bindings on existing template roots without replacing containers.</summary>
internal static class DashboardTemplateBindings
{
    public static void Update(DependencyObject root, Func<object, object> select)
    {
        if (root is FrameworkElement element && XamlBindingHelper.GetDataTemplateComponent(element) is { } bindings)
        {
            if (element.DataContext is { } item) bindings.ProcessBindings(select(item), 0, 0, out _);
            return;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            Update(VisualTreeHelper.GetChild(root, i), select);
    }
}
