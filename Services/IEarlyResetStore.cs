using CodexBarWin.Helpers;

namespace CodexBarWin.Services;

/// <summary>
/// Store for persisting and querying detected early reset events.
/// </summary>
public interface IEarlyResetStore
{
    /// <summary>
    /// Asynchronously loads stored early reset events from disk. Corrupt files are renamed aside and ignored.
    /// </summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Asynchronously saves in-memory early reset events to disk atomically.
    /// </summary>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>
    /// Records a detected early reset event. Returns true if the event is new, or false if already recorded.
    /// </summary>
    bool Record(EarlyReset @event);

    /// <summary>
    /// Marks an early reset event as having notified the user via toast.
    /// </summary>
    bool MarkNotified(EarlyReset @event);

    /// <summary>
    /// Checks whether an early reset event was already notified.
    /// </summary>
    bool IsNotified(EarlyReset @event);

    /// <summary>
    /// Returns all active events within the 24-hour display window relative to <paramref name="nowUtc"/>.
    /// </summary>
    IReadOnlyList<EarlyReset> GetActive(DateTime nowUtc);

    /// <summary>
    /// Returns events for a specific provider and window key detected since <paramref name="sinceUtc"/>.
    /// </summary>
    IReadOnlyList<EarlyReset> GetRecent(string provider, string windowKey, DateTime sinceUtc);
}
