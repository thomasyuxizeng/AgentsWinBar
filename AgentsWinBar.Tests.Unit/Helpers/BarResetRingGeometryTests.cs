using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class BarResetRingGeometryTests
{
    private const float Tolerance = 0.0001f;

    [TestMethod]
    [DataRow(5.0, 2.5f)]
    [DataRow(6.0, 3f)]
    [DataRow(10.0, 3f)]
    [DataRow(0.0, 0f)]
    public void TrackRadius_BarHeight_IsClampedToHalfHeight(double height, float expected)
    {
        BarResetRingGeometry.TrackRadius(height).Should().Be(expected);
    }

    [TestMethod]
    [DataRow(5.0)]
    [DataRow(6.0)]
    [DataRow(8.0)]
    [DataRow(12.0)]
    [DataRow(20.0)]
    public void For_BarHeight_CentreLineIsAPill(double height)
    {
        var layout = BarResetRingGeometry.For(56, height);

        layout.CornerRadius.Should().BeApproximately(layout.GeometrySize.Y / 2, Tolerance);
    }

    [TestMethod]
    [DataRow(5.0)]
    [DataRow(6.0)]
    [DataRow(8.0)]
    [DataRow(12.0)]
    [DataRow(20.0)]
    public void For_BarHeight_InnerEdgeJustInsideAndOuterEdgeWellOutsideTheBar(double height)
    {
        var layout = BarResetRingGeometry.For(56, height);
        var pad = -layout.VisualOffset.Y;
        var centreTop = layout.GeometryOffset.Y - pad;   // centre line, relative to the track's top edge (negative = above it)
        var innerOutside = -(centreTop + layout.StrokeThickness / 2);
        var outerOutside = -(centreTop - layout.StrokeThickness / 2);
        var featherInner = -(centreTop + layout.FeatherThickness / 2);
        var featherOuter = -(centreTop - layout.FeatherThickness / 2);

        innerOutside.Should().BeApproximately(-0.5f, Tolerance);
        innerOutside.Should().BeLessThan(0f, "the opaque inner edge hides the bar's anti-aliasing hairline");
        outerOutside.Should().BeApproximately(2.5f, Tolerance);
        featherInner.Should().BeApproximately(-1f, Tolerance);
        featherOuter.Should().BeApproximately(3f, Tolerance, "the overall size is unchanged: the outer edge stays 3 px out");
        layout.StrokeThickness.Should().Be(3f);
        layout.FeatherThickness.Should().Be(4f);
    }

    [TestMethod]
    [DataRow(5.0)]
    [DataRow(6.0)]
    [DataRow(8.0)]
    [DataRow(12.0)]
    [DataRow(20.0)]
    public void For_BarHeight_RingIsConcentricWithPillBar(double height)
    {
        var layout = BarResetRingGeometry.For(56, height);

        // Opaque inner edge radius = bar radius (h/2) - 0.5, feather outer edge radius = h/2 + 3
        (layout.CornerRadius - layout.StrokeThickness / 2).Should().BeApproximately((float)height / 2 - 0.5f, Tolerance);
        (layout.CornerRadius + layout.FeatherThickness / 2).Should().BeApproximately((float)height / 2 + 3f, Tolerance);
    }

    [TestMethod]
    [DataRow(5.0)]
    [DataRow(8.0)]
    [DataRow(10.0)]
    [DataRow(12.0)]
    [DataRow(20.0)]
    public void For_BarHeight_TrackCornerStaysInsideTheRingOuterEdge(double height)
    {
        // Above 6 px the track's corners (radius 3) are tighter than the pill ring; they must not poke out of it
        var layout = BarResetRingGeometry.For(56, height);
        var r = BarResetRingGeometry.TrackRadius(height);
        var cornerPoint = r * (1 - 1 / MathF.Sqrt(2));          // outermost point of the track's corner arc
        var ringCentre = (float)height / 2;                      // centre of the ring's left end (track coordinates)
        var distance = MathF.Sqrt(2) * (ringCentre - cornerPoint);

        distance.Should().BeLessThanOrEqualTo(layout.CornerRadius + layout.FeatherThickness / 2);
    }

    [TestMethod]
    [DataRow(5.0)]
    [DataRow(12.0)]
    public void For_Track_ShapeVisualContainsTheWholeStrokeWithAntiAliasMargin(double height)
    {
        var layout = BarResetRingGeometry.For(56, height);
        var half = layout.FeatherThickness / 2;

        (layout.GeometryOffset.X - half).Should().BeGreaterThanOrEqualTo(BarResetRingGeometry.AntiAliasMargin - Tolerance);
        (layout.GeometryOffset.Y - half).Should().BeGreaterThanOrEqualTo(BarResetRingGeometry.AntiAliasMargin - Tolerance);
        (layout.VisualSize.X - (layout.GeometryOffset.X + layout.GeometrySize.X + half)).Should().BeGreaterThanOrEqualTo(BarResetRingGeometry.AntiAliasMargin - Tolerance);
        (layout.VisualSize.Y - (layout.GeometryOffset.Y + layout.GeometrySize.Y + half)).Should().BeGreaterThanOrEqualTo(BarResetRingGeometry.AntiAliasMargin - Tolerance);
    }

    [TestMethod]
    public void For_Track_FeatherIsSlightlyWiderAndFainterThanTheMainStroke()
    {
        var layout = BarResetRingGeometry.For(56, 6);

        (layout.FeatherThickness - layout.StrokeThickness).Should().BeApproximately(2 * BarResetRingGeometry.Feather, Tolerance);
        BarResetRingGeometry.Feather.Should().Be(0.5f);
        BarResetRingGeometry.FeatherOpacity.Should().BeInRange(0.3f, 0.4f);
    }

    [TestMethod]
    public void PathLength_Pill_IsStraightRunsPlusTwoSemicircles()
    {
        var layout = BarResetRingGeometry.For(56, 6);
        var straight = layout.GeometrySize.X - layout.GeometrySize.Y;

        layout.PathLength.Should().BeApproximately(2 * straight + MathF.PI * layout.GeometrySize.Y, 0.001f);
    }

    [TestMethod]
    public void For_Track_RingIsCentredOnTheTrack()
    {
        var layout = BarResetRingGeometry.For(56, 6);

        (layout.VisualOffset.X + layout.GeometryOffset.X + layout.GeometrySize.X / 2).Should().BeApproximately(28f, Tolerance);
        (layout.VisualOffset.Y + layout.GeometryOffset.Y + layout.GeometrySize.Y / 2).Should().BeApproximately(3f, Tolerance);
    }

    [TestMethod]
    [DataRow(0.0, 0.0)]
    [DataRow(1.0, 1.0)]
    [DataRow(56.0, 0.5)]
    [DataRow(1.5, 5.0)]
    [DataRow(-3.0, 5.0)]
    [DataRow(double.NaN, double.PositiveInfinity)]
    public void For_TinyOrInvalidTrack_NeverNegativeOrNaN(double width, double height)
    {
        var layout = BarResetRingGeometry.For(width, height);
        float[] values =
        [
            layout.VisualSize.X, layout.VisualSize.Y, layout.GeometryOffset.X, layout.GeometryOffset.Y,
            layout.GeometrySize.X, layout.GeometrySize.Y, layout.CornerRadius, layout.StrokeThickness,
            layout.FeatherThickness, layout.PathLength
        ];

        values.Should().AllSatisfy(v => float.IsFinite(v).Should().BeTrue());
        values.Should().AllSatisfy(v => v.Should().BeGreaterThanOrEqualTo(0f));
        layout.CornerRadius.Should().BeLessThanOrEqualTo(Math.Min(layout.GeometrySize.X, layout.GeometrySize.Y) / 2 + Tolerance);
        layout.StrokeThickness.Should().BeGreaterThan(0f);
    }
}
