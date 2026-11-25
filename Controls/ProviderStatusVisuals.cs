using CodexBarWin.Helpers;
using CodexBarWin.Models;
using Microsoft.UI.Xaml.Media;

namespace CodexBarWin.Controls;

/// <summary>WinUI brush conversion for the shared, pure status palette.</summary>
internal static class ProviderStatusVisuals
{
    // One brush per colour, shared by every window (all run on the one UI thread): a refresh then hands the strip, the
    // overlay and the cards the same brush instead of a new one each time
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
}
