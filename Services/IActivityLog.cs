using CodexBarWin.Models;

namespace CodexBarWin.Services;

public interface IActivityLog
{
    void RecordRefresh(DashboardRefreshEntry entry);
    void RecordAlert(DashboardAlertEntry entry);
    IReadOnlyList<DashboardRefreshEntry> RecentRefreshes { get; }
    IReadOnlyList<DashboardAlertEntry> RecentAlerts { get; }
}
