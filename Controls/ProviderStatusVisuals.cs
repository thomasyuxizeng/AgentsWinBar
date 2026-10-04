using CodexBarWin.Helpers;
using CodexBarWin.Models;
using Microsoft.UI.Xaml.Media;

namespace CodexBarWin.Controls;

/// <summary>WinUI brush conversion for the shared, pure status palette.</summary>
internal static class ProviderStatusVisuals
{
    public static Brush Brush(ProviderStatus? status) =>
        (Brush)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(
            typeof(Brush), ProviderStatusPresentation.ColorHex(status));
}
