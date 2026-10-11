using CodexBarWin.Helpers;

namespace CodexBarWin.Models;

/// <summary>
/// Persisted root structure for early-resets.json.
/// </summary>
public sealed record EarlyResetStoreFile
{
    public int Version { get; init; } = 1;
    public List<EarlyResetEntry> Events { get; init; } = [];
    public List<EarlyResetWindowReading> Baselines { get; init; } = [];
    /// <summary>Per-window ambiguity guards, retained until a newer unique reading establishes a baseline.</summary>
    public Dictionary<string, DateTime> InvalidatedAtUtc { get; init; } = [];
}

/// <summary>
/// A recorded early reset event with notification state.
/// </summary>
public sealed record EarlyResetEntry
{
    public EarlyReset Event { get; init; } = default!;
    /// <summary>Notification reserved before its toast attempt, so a restart cannot repeat the toast.</summary>
    public bool Notified { get; init; }
    public DateTime? NotifiedAtUtc { get; init; }
}
