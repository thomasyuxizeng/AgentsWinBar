using AgentsWinBar.Models;

namespace AgentsWinBar.Services;

/// <summary>Bounded session activity. Snapshots never expose the mutable lists.</summary>
public sealed class ActivityLog : IActivityLog
{
    private readonly object _gate = new();
    private readonly List<DashboardRefreshEntry> _refreshes = [];
    private readonly List<DashboardAlertEntry> _alerts = [];

    public IReadOnlyList<DashboardRefreshEntry> RecentRefreshes { get { lock (_gate) return _refreshes.ToArray(); } }
    public IReadOnlyList<DashboardAlertEntry> RecentAlerts { get { lock (_gate) return _alerts.ToArray(); } }

    public void RecordRefresh(DashboardRefreshEntry entry)
    {
        if (entry == null) return;
        lock (_gate)
        {
            InsertDescending(_refreshes, entry, 40, static item => item.TimestampUtc);
        }
    }

    public void RecordAlert(DashboardAlertEntry entry)
    {
        if (entry == null) return;
        lock (_gate)
        {
            InsertDescending(_alerts, entry, 20, static item => item.TimestampUtc);
        }
    }

    private static void InsertDescending<T>(List<T> items, T entry, int capacity, Func<T, DateTime> getTimestamp)
    {
        var timestamp = getTimestamp(entry);
        if (items.Count == 0 || timestamp >= getTimestamp(items[0]))
        {
            items.Insert(0, entry);
        }
        else
        {
            var index = 1;
            while (index < items.Count && timestamp < getTimestamp(items[index])) index++;
            items.Insert(index, entry);
        }
        if (items.Count > capacity) items.RemoveRange(capacity, items.Count - capacity);
    }
}
