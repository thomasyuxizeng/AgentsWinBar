using CodexBarWin.Models;

namespace CodexBarWin.Services;

/// <summary>Persists the last complete cost result per provider.</summary>
public interface ICostCacheStore
{
    Task<IReadOnlyDictionary<string, CostData>> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(IReadOnlyDictionary<string, CostData> items, CancellationToken ct = default);
}
