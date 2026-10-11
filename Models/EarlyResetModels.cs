using CodexBarWin.Helpers;

namespace CodexBarWin.Models;

/// <summary>
/// Persisted root structure for early-resets.json.
/// </summary>
public sealed record EarlyResetStoreFile
{
    public int Version { get; init; } = 1;
    public List<EarlyResetEntry> Events { get; init; } = [];
}

/// <summary>
/// A recorded early reset event with notification state.
/// </summary>
public sealed record EarlyResetEntry
{
    public EarlyReset Event { get; init; } = default!;
    public bool Notified { get; init; }
    public DateTime? NotifiedAtUtc { get; init; }
}
