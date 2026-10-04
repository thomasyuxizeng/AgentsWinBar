using CodexBarWin.Models;

namespace CodexBarWin.Services;

public interface IDashboardService
{
    DashboardSnapshot GetSnapshot(IReadOnlyList<UsageData> providers, DateTime? lastRefreshUtc);
    Task RefreshDiagnosticsAsync(CancellationToken ct = default);
}
