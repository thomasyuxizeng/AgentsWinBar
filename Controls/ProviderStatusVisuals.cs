using CodexBarWin.Helpers;
using CodexBarWin.Models;
using Microsoft.UI.Xaml.Media;

namespace CodexBarWin.Controls;

/// <summary>WinUI brush conversion for the shared, pure status palette.</summary>
internal static class ProviderStatusVisuals
{
    // One brush per colour for persistent widgets (overlay/strip/card) so InPlaceRow keeps brush identity.
    // Transient windows (Dashboard) use CreateBrush() so Brush DependencyObjects are never shared across windows.
    private static readonly Dictionary<string, Brush> Brushes = new(StringComparer.OrdinalIgnoreCase);

    public static Brush Brush(ProviderStatus? status)
    {
        var hex = ProviderStatusPresentation.ColorHex(status);
        if (!Brushes.TryGetValue(hex, out var brush))
        {
            brush = (Brush)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Brush), hex);
            Brushes[hex] = brush;
        }
        return brush;
    }

    /// <summary>Creates a fresh brush instance for the status colour so transient windows never share Brush DependencyObjects.</summary>
    public static Brush CreateBrush(ProviderStatus? status)
    {
        var hex = ProviderStatusPresentation.ColorHex(status);
        return (Brush)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Brush), hex);
    }
}
