using System.Numerics;

namespace CodexBarWin.Helpers;

/// <summary>
/// Placement of the reset ring around a track, in the track's coordinates (DIPs, origin at the track's top-left).
/// The shape visual is padded beyond the ring because a ShapeVisual clips its shapes to its own size.
/// </summary>
/// <param name="VisualOffset">Offset of the shape visual (negative: it starts outside the track).</param>
/// <param name="VisualSize">Size of the shape visual.</param>
/// <param name="GeometryOffset">Offset of the ring's centre-line rectangle inside the shape visual.</param>
/// <param name="GeometrySize">Size of the ring's centre-line rectangle.</param>
/// <param name="CornerRadius">Corner radius of the centre line: half its smaller side, so the ring is a pill.</param>
/// <param name="StrokeThickness">Thickness of the opaque main stroke.</param>
/// <param name="FeatherThickness">Thickness of the faint feather stroke under it (same centre line, wider on both sides).</param>
internal readonly record struct BarResetRingLayout(
    Vector2 VisualOffset,
    Vector2 VisualSize,
    Vector2 GeometryOffset,
    Vector2 GeometrySize,
    float CornerRadius,
    float StrokeThickness,
    float FeatherThickness)
{
    /// <summary>Length of the ring's centre line (straight runs plus the two semicircular ends).</summary>
    public float PathLength =>
        2 * Math.Max(0, GeometrySize.X - 2 * CornerRadius) + 2 * Math.Max(0, GeometrySize.Y - 2 * CornerRadius) + 2 * MathF.PI * CornerRadius;
}

/// <summary>
/// Geometry of the reset ring: a pill (stadium) concentric with the bar. Its opaque inner edge sits
/// <see cref="InnerInset"/> inside the track edge (hiding the bar's dark anti-aliasing hairline) and its outer edge
/// <see cref="OuterOutset"/> beyond it. The edges are feathered: the opaque main stroke stops <see cref="Feather"/>
/// short of the outer edge, and a faint, wider stroke on the same centre line extends <see cref="Feather"/> beyond
/// the main stroke on both sides (outwards up to the outer edge, inwards over the bar).
/// </summary>
internal static class BarResetRingGeometry
{
    /// <summary>Distance of the ring's inner edge inside the track edge (limited to half the bar on tiny bars).</summary>
    public const float InnerInset = 0.5f;

    /// <summary>Distance of the ring's outer edge (the feather's outer edge) beyond the track edge.</summary>
    public const float OuterOutset = 3f;

    /// <summary>Width of the faint feather band on each side of the opaque main stroke.</summary>
    public const float Feather = 0.5f;

    /// <summary>Opacity of the feather stroke.</summary>
    public const float FeatherOpacity = 0.35f;

    /// <summary>Extra room around the outer edge inside the shape visual, so its anti-aliased edge is not clipped.</summary>
    public const float AntiAliasMargin = 1f;

    /// <summary>The track's corner radius (UsageMeter rectangles use RadiusX/Y 3; thin bars clamp it to half the height).</summary>
    public const float TrackCornerRadius = 3f;

    /// <summary>The track's real corner radius: <see cref="TrackCornerRadius"/>, limited to half the bar height.</summary>
    public static float TrackRadius(double trackHeight) => (float)Math.Min(TrackCornerRadius, Sanitize(trackHeight) / 2);

    public static BarResetRingLayout For(double trackWidth, double trackHeight)
    {
        var w = Sanitize(trackWidth);
        var h = Sanitize(trackHeight);

        var inner = Math.Min(InnerInset, Math.Min(w, h) / 2);
        var stroke = inner + OuterOutset - Feather;

        // Centre line distance beyond the track edge (main stroke spans -inner .. OuterOutset - Feather)
        var outward = (OuterOutset - Feather - inner) / 2;
        var size = new Vector2(w + 2 * outward, h + 2 * outward);

        // Pill: exactly half the smaller side. Opaque inner edge radius = h/2 - inner, feather outer edge = h/2 +
        // OuterOutset, all concentric with a pill-shaped bar (radius h/2).
        var radius = Math.Min(size.X, size.Y) / 2;

        const float pad = OuterOutset + AntiAliasMargin;
        return new BarResetRingLayout(
            VisualOffset: new Vector2(-pad, -pad),
            VisualSize: new Vector2(w + 2 * pad, h + 2 * pad),
            GeometryOffset: new Vector2(pad - outward, pad - outward),
            GeometrySize: size,
            CornerRadius: radius,
            StrokeThickness: stroke,
            FeatherThickness: stroke + 2 * Feather);
    }

    private static float Sanitize(double value) => double.IsFinite(value) && value > 0 ? (float)value : 0f;
}
