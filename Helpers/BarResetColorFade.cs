namespace AgentsWinBar.Helpers;

/// <summary>An sRGB colour with alpha (pure stand-in for Windows.UI.Color, so the maths stays testable).</summary>
internal readonly record struct BarResetRgba(byte R, byte G, byte B, byte A = 255);

/// <summary>
/// Colour keyframes for the reset fill's fade from the previous bar colour to green. Blending in RGB passes through
/// muddy mid-tones (red to brown, amber to olive), so the colours are mixed in HSL instead: saturation and lightness
/// are interpolated linearly and the hue takes the shorter way round the wheel, so red passes through bright orange
/// and yellow-green, blue through cyan and teal. (HSL rather than OKLCH on purpose: OKLCH holds perceived lightness,
/// which turns red-to-green into dark ochre; HSL keeps the mid colours bright.) The hue path is computed here rather
/// than left to the compositor, whose HSL blending could take the long way (e.g. red through magenta and blue).
/// </summary>
internal static class BarResetColorFade
{
    /// <summary>Keyframes per fade; the compositor blends linearly between these short, close steps.</summary>
    public const int Steps = 10;

    /// <summary>Below this HSL saturation a colour counts as grey and takes the other end's hue.</summary>
    private const double GreySaturation = 0.08;

    /// <summary>
    /// <see cref="Steps"/> + 1 keyframes of the fade. Times are evenly spaced 0..1; each colour is the HSL mix at the
    /// eased progress of that time, so linear blending between keyframes follows the cubic bezier (x1, y1, x2, y2).
    /// </summary>
    public static IReadOnlyList<(float Time, BarResetRgba Color)> Keyframes(BarResetRgba from, BarResetRgba to, float x1, float y1, float x2, float y2)
    {
        var frames = new (float, BarResetRgba)[Steps + 1];
        for (var i = 0; i <= Steps; i++)
        {
            var t = i / (float)Steps;
            frames[i] = (t, Mix(from, to, CubicBezier(x1, y1, x2, y2, t)));
        }
        return frames;
    }

    /// <summary>Mix of <paramref name="from"/> and <paramref name="to"/> at <paramref name="progress"/> (0..1) in HSL.</summary>
    public static BarResetRgba Mix(BarResetRgba from, BarResetRgba to, double progress)
    {
        var p = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        if (p <= 0) return from;
        if (p >= 1) return to;

        var (h1, s1, l1) = ToHsl(from);
        var (h2, s2, l2) = ToHsl(to);
        if (s1 < GreySaturation) h1 = h2;
        if (s2 < GreySaturation) h2 = h1;

        var dh = h2 - h1;
        if (dh > 180) dh -= 360;
        if (dh < -180) dh += 360;

        var h = (h1 + dh * p + 360) % 360;
        var s = s1 + (s2 - s1) * p;
        var l = l1 + (l2 - l1) * p;
        var alpha = (byte)Math.Round(from.A + (to.A - from.A) * p);
        var (r, g, b) = FromHsl(h, s, l);
        return new BarResetRgba(ToByte(r), ToByte(g), ToByte(b), alpha);
    }

    /// <summary>HSL of a colour: hue in degrees 0..360, saturation and lightness 0..1.</summary>
    public static (double H, double S, double L) ToHsl(BarResetRgba color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;
        if (d <= 0) return (0, 0, l);

        var s = d / (1 - Math.Abs(2 * l - 1));
        double h;
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, Math.Clamp(s, 0, 1), l);
    }

    /// <summary>Value of the cubic bezier easing (0,0)-(x1,y1)-(x2,y2)-(1,1) at time <paramref name="t"/>.</summary>
    public static float CubicBezier(float x1, float y1, float x2, float y2, float t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;

        // Find the curve parameter whose x is t (x is monotonic for control x in 0..1), then return its y
        double lo = 0, hi = 1, u = t;
        for (var i = 0; i < 40; i++)
        {
            u = (lo + hi) / 2;
            if (Bezier(x1, x2, u) < t) lo = u; else hi = u;
        }
        return (float)Bezier(y1, y2, u);
    }

    private static double Bezier(double p1, double p2, double u)
    {
        var v = 1 - u;
        return 3 * v * v * u * p1 + 3 * v * u * u * p2 + u * u * u;
    }

    private static (double R, double G, double B) FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return (r + m, g + m, b + m);
    }

    private static byte ToByte(double c) => (byte)Math.Round(Math.Clamp(c, 0, 1) * 255);
}
