using AgentsWinBar.Models;

namespace AgentsWinBar.Services;

public interface IGeminiSpendService
{
    TimeZoneInfo Zone { get; }
    Task<GeminiApiSpendInfo> GetAsync(CancellationToken ct);
}
