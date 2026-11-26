using AgentsWinBar.Models;

namespace AgentsWinBar.Services;

public interface IActivityLog
{
    void RecordRefresh(DashboardRefreshEntry entry);
    void RecordAlert(DashboardAlertEntry entry);
    IReadOnlyList<DashboardRefreshEntry> RecentRefreshes { get; }
    IReadOnlyList<DashboardAlertEntry> RecentAlerts { get; }
}
