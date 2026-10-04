using CodexBarWin.Models;

namespace CodexBarWin.Services;

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
            _refreshes.Insert(0, entry);
            _refreshes.Sort((a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));
            if (_refreshes.Count > 40) _refreshes.RemoveRange(40, _refreshes.Count - 40);
        }
    }

    public void RecordAlert(DashboardAlertEntry entry)
    {
        if (entry == null) return;
        lock (_gate)
        {
            _alerts.Insert(0, entry);
            _alerts.Sort((a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));
            if (_alerts.Count > 20) _alerts.RemoveRange(20, _alerts.Count - 20);
        }
    }
}
