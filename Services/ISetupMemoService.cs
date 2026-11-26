namespace AgentsWinBar.Services;

/// <summary>A recent successful CLI setup check that avoids probing WSL on launch.</summary>
public interface ISetupMemoService
{
    Task<bool> IsValidAsync(CancellationToken ct = default);
    Task SaveAsync(string? version, CancellationToken ct = default);
    Task InvalidateAsync(CancellationToken ct = default);
}
