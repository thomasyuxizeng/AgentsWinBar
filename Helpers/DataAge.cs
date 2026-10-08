namespace CodexBarWin.Helpers;

/// <summary>
/// A fetch can succeed while returning an old reading: the WSL shim serves its last good Claude result when
/// both routes fail. The provider's own updatedAt tells the reading's real age.
/// </summary>
public static class DataAge
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(15);

    /// <summary>The reading's own time (UTC) when it lags the fetch by more than <see cref="Threshold"/>; otherwise null.</summary>
    public static DateTime? OldReadingUtc(DateTime fetchedAtUtc, DateTime? dataUpdatedAtUtc)
    {
        if (dataUpdatedAtUtc is not { } updated) return null;
        var reading = ToUtc(updated);
        return ToUtc(fetchedAtUtc) - reading > Threshold ? reading : null;
    }

    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value
    };
}
