using AgentsWinBar.Models;

namespace AgentsWinBar.Helpers;

/// <summary>The per-provider "show graphs" choice on the dashboard, kept in <see cref="ProviderConfig.ShowGraphs"/>.</summary>
public static class GraphVisibility
{
    /// <summary>Ids of the providers whose graphs are switched off.</summary>
    public static IReadOnlyList<string> HiddenIds(IEnumerable<ProviderConfig>? providers) =>
        providers?.Where(p => p != null && !p.ShowGraphs).Select(p => p.Id).ToList() ?? [];

    /// <summary>Sets one provider's choice; false when the provider is unknown or the value is already that.</summary>
    public static bool TrySet(IEnumerable<ProviderConfig>? providers, string providerId, bool visible)
    {
        var provider = providers?.FirstOrDefault(p => p != null && string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider == null || provider.ShowGraphs == visible) return false;
        provider.ShowGraphs = visible;
        return true;
    }
}
