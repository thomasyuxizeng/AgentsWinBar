using CodexBarWin.Models;

namespace CodexBarWin.Services;

public interface IGeminiSpendService
{
    TimeZoneInfo Zone { get; }
    Task<GeminiApiSpendInfo> GetAsync(CancellationToken ct);
}
