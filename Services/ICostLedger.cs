using AgentsWinBar.Models;

namespace AgentsWinBar.Services;

/// <summary>
/// Permanent cost ledger interface. Lifetime costs survive local log rotation,
/// allowing reliable lifetime queries and 30-day lookups.
/// Queries read memory only; before loading completes they return no data and queue a background load.
/// </summary>
public interface ICostLedger
{
    string FilePath { get; }

    Task LoadAsync(CancellationToken ct = default);

    Task RecordScanAsync(CostData scan, DateTime? nowUtc = null, CancellationToken ct = default);

    Task RecordScansAsync(IReadOnlyList<CostData> scans, DateTime? nowUtc = null, CancellationToken ct = default);

    CostLedgerLifetimeResult GetLifetime(string? provider = null);

    CostLedgerRangeResult GetRange(string? provider = null, DateOnly fromDay = default, DateOnly toDay = default);

    CostLedgerLast30DaysResult GetLast30Days(string? provider = null, DateOnly? asOfDay = null);
}
