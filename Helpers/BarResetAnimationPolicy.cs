namespace CodexBarWin.Helpers;

/// <summary>
/// Pure decision logic for the usage-bar reset animation: whether a new reset timestamp should make the bar play it.
/// </summary>
internal static class BarResetAnimationPolicy
{
    /// <summary>How recent a detected reset must be for the bar to still animate it.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

    /// <summary>How far into the future a timestamp may be before it is treated as clock skew and ignored.</summary>
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromSeconds(2);

    /// <param name="playbackEnabled">The user's global reset-animation setting (see BarResetAnimation.PlaybackEnabled); false never plays.</param>
    /// <param name="visibleSinceUtc">When the screen became visible again after this reset was detected while it was locked
    /// (see <see cref="ResetAnimationVisibility"/>); the age is then counted from there, so the animation plays once on return.</param>
    public static bool ShouldPlay(DateTime? newValue, DateTime? oldValue, DateTime nowUtc, bool animationsEnabled, bool isShown, bool playbackEnabled = true,
        DateTime? visibleSinceUtc = null)
    {
        if (!playbackEnabled || !animationsEnabled || !isShown) return false;
        if (newValue is not { } value || value == oldValue) return false;

        var detected = value.ToUniversalTime();
        var reference = visibleSinceUtc is { } since && since.ToUniversalTime() > detected ? since.ToUniversalTime() : detected;
        var age = nowUtc - reference;
        return age >= -MaxFutureSkew && age <= MaxAge;
    }
}
