using System.Collections.ObjectModel;
using CodexBarWin.Models;

namespace CodexBarWin.Helpers;

/// <summary>
/// Keeps the displayed provider list in step with the providers enabled in Settings.
/// </summary>
public static class ProviderListSynchronizer
{
    /// <summary>
    /// Removes providers that are no longer enabled, adds a loading placeholder for newly enabled ones,
    /// and puts everything in the configured order. Existing entries (and their data) are kept.
    /// </summary>
    /// <param name="providers">The displayed list, updated in place.</param>
    /// <param name="enabledIdsInOrder">Enabled provider ids, in display order.</param>
    public static void Sync(ObservableCollection<UsageData> providers, IReadOnlyList<string> enabledIdsInOrder)
    {
        // Remove providers that were switched off
        for (var i = providers.Count - 1; i >= 0; i--)
        {
            if (!enabledIdsInOrder.Contains(providers[i].Provider, StringComparer.OrdinalIgnoreCase))
            {
                providers.RemoveAt(i);
            }
        }

        // Add the missing ones and put everything in order
        for (var target = 0; target < enabledIdsInOrder.Count; target++)
        {
            var id = enabledIdsInOrder[target];
            var current = IndexOf(providers, id);

            if (current < 0)
            {
                providers.Insert(target, new UsageData
                {
                    Provider = id,
                    IsLoading = true,
                    FetchedAt = DateTime.UtcNow
                });
            }
            else if (current != target)
            {
                providers.Move(current, target);
            }
        }
    }

    private static int IndexOf(ObservableCollection<UsageData> providers, string id)
    {
        for (var i = 0; i < providers.Count; i++)
        {
            if (providers[i].Provider.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
