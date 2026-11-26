using AgentsWinBar.Models;

namespace AgentsWinBar.Services;

public interface IApiBillingService
{
    Task<ApiCreditsInfo> GetAsync(bool force, CancellationToken ct);
}
