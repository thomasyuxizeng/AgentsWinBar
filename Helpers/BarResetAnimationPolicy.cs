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

    public static bool ShouldPlay(DateTime? newValue, DateTime? oldValue, DateTime nowUtc, bool animationsEnabled, bool isShown)
    {
        if (!animationsEnabled || !isShown) return false;
        if (newValue is not { } value || value == oldValue) return false;

        var age = nowUtc - value.ToUniversalTime();
        return age >= -MaxFutureSkew && age <= MaxAge;
    }
}
