namespace CodexBarWin.Helpers;

/// <summary>
/// Phase timings (milliseconds from the start of a run) of the bar reset animation:
/// a ring draws itself; b the previous fill cross-fades its colour to solid green while c the ring fades out (same
/// span); d the green fill collapses to the left; e the glow takes over from the collapsed green at the left end and
/// sweeps the track (consecutive, overlapping only by <see cref="SweepOverlapMs"/>); f resting look.
/// </summary>
internal static class BarResetTimeline
{
    public const int RingStartMs = 0;
    public const int RingMs = 900;
    public const int RingFullMs = RingStartMs + RingMs;

    /// <summary>The ring stays invisible until this point, so its zero-length stroke start never shows.</summary>
    public const int RingRevealMs = 40;

    public const int FillStartMs = RingFullMs;
    public const int FillMs = 500;
    public const int FillEndMs = FillStartMs + FillMs;

    public const int RingFadeStartMs = FillStartMs;
    public const int RingFadeMs = FillMs;
    public const int RingFadeEndMs = RingFadeStartMs + RingFadeMs;

    public const int CollapseStartMs = FillEndMs;
    public const int CollapseMs = 900;
    public const int CollapseEndMs = CollapseStartMs + CollapseMs;

    /// <summary>The collapsing fill stays opaque until this point, then its last sliver fades into the glow.</summary>
    public const int CollapseFadeStartMs = CollapseEndMs - CollapseMs / 10;

    /// <summary>The sweep starts this long before the collapse ends, so there is no dead frame at the handoff.</summary>
    public const int SweepOverlapMs = 50;

    public const int SweepStartMs = CollapseEndMs - SweepOverlapMs;
    public const int SweepMs = 1250;
    public const int SweepEndMs = SweepStartMs + SweepMs;

    /// <summary>The glow fades in over this first part of the sweep, while the green's last sliver fades out.</summary>
    public const int GlowRevealMs = 150;

    /// <summary>Share of the sweep, at its end, over which the glow fades out.</summary>
    public const float SweepFadeShare = 0.25f;

    /// <summary>Length of the whole run: the end of the last phase.</summary>
    public const int TotalMs = SweepEndMs;

    /// <summary>
    /// Gentle ease-in-out cubic bezier (ring draw, colour fill, collapse): mild at both ends, no overshoot. The
    /// collapse therefore lands softly, at zero speed.
    /// </summary>
    public const float GentleX1 = 0.35f, GentleY1 = 0f, GentleX2 = 0.65f, GentleY2 = 1f;

    /// <summary>
    /// Sweep curve: starts at zero speed (matching the collapse's soft landing, so the handoff has no jump), gentle
    /// ease-in, then a longer ease-out. No overshoot.
    /// </summary>
    public const float SweepX1 = 0.4f, SweepY1 = 0f, SweepX2 = 0.2f, SweepY2 = 1f;

    /// <summary>Position of <paramref name="ms"/> on the whole run as a 0-1 keyframe fraction.</summary>
    public static float Fraction(int ms) => Math.Clamp(ms / (float)TotalMs, 0f, 1f);
}
