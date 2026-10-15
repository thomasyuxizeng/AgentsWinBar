using CodexBarWin.Models;

namespace CodexBarWin.Services;

public interface IApiBillingService
{
    Task<ApiCreditsInfo> GetAsync(bool force, CancellationToken ct);
}
