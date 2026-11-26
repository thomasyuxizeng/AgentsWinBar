using AwesomeAssertions;
using AgentsWinBar.Helpers;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class BarResetColorFadeTests
{
    private static readonly BarResetRgba Green = new(0x28, 0xD8, 0x64);

    // Bar colours from UsageMeter: critical red, warning amber, main blue, Claude orange, OpenAI teal
    [TestMethod]
    [DataRow((byte)232, (byte)17, (byte)35)]
    [DataRow((byte)255, (byte)185, (byte)0)]
    [DataRow((byte)0, (byte)120, (byte)212)]
    [DataRow((byte)0xD9, (byte)0x77, (byte)0x57)]
    [DataRow((byte)0x10, (byte)0xA3, (byte)0x7F)]
    public void Mix_BarColourToGreen_MidColourStaysCleanAndSaturated(byte r, byte g, byte b)
    {
        var from = new BarResetRgba(r, g, b);
        var mid = BarResetColorFade.Mix(from, Green, 0.5);

        var (_, s, l) = BarResetColorFade.ToHsl(mid);
        s.Should().BeGreaterThan(0.6, "the mid colour must stay saturated");
        l.Should().BeInRange(0.3, 0.7);

        // Perceptual check (OKLCH): no darkening and no loss of colourfulness compared with the two ends
        var (fromL, fromC) = Oklch(from);
        var (greenL, greenC) = Oklch(Green);
        var (midL, midC) = Oklch(mid);
        midL.Should().BeGreaterThanOrEqualTo(Math.Min(fromL, greenL) - 0.01);
        midC.Should().BeGreaterThanOrEqualTo(0.75 * Math.Min(fromC, greenC));
    }

    [TestMethod]
    [DataRow((byte)232, (byte)17, (byte)35)]
    [DataRow((byte)255, (byte)185, (byte)0)]
    [DataRow((byte)0, (byte)120, (byte)212)]
    public void Mix_BarColourToGreen_IsMoreColourfulThanAnRgbBlend(byte r, byte g, byte b)
    {
        var from = new BarResetRgba(r, g, b);
        var rgbMid = new BarResetRgba((byte)((r + Green.R) / 2), (byte)((g + Green.G) / 2), (byte)((b + Green.B) / 2));

        Oklch(BarResetColorFade.Mix(from, Green, 0.5)).C.Should().BeGreaterThan(Oklch(rgbMid).C);
    }

    [TestMethod]
    public void Mix_RedToGreen_PassesThroughOrangeAndYellowNotMagentaOrBlue()
    {
        var red = new BarResetRgba(232, 17, 35);
        for (var p = 0.1; p < 1; p += 0.1)
        {
            var (h, _, _) = BarResetColorFade.ToHsl(BarResetColorFade.Mix(red, Green, p));
            (h < 150 || h > 350).Should().BeTrue($"hue {h:F0} at {p:F1} must go the short way via orange/yellow");
        }
    }

    [TestMethod]
    public void Mix_BlueToGreen_PassesThroughCyanAndTeal()
    {
        var (h, _, _) = BarResetColorFade.ToHsl(BarResetColorFade.Mix(new BarResetRgba(0, 120, 212), Green, 0.5));

        h.Should().BeInRange(160, 190);
    }

    [TestMethod]
    public void Mix_Ends_AreExact()
    {
        var red = new BarResetRgba(232, 17, 35, 200);

        BarResetColorFade.Mix(red, Green, 0).Should().Be(red);
        BarResetColorFade.Mix(red, Green, 1).Should().Be(Green);
        BarResetColorFade.Mix(red, Green, double.NaN).Should().Be(red);
    }

    [TestMethod]
    public void Mix_TranslucentStart_EndsFullyOpaque()
    {
        var from = new BarResetRgba(232, 17, 35, 0x88);

        BarResetColorFade.Mix(from, Green, 0.5).A.Should().BeInRange((byte)0xC0, (byte)0xC8);
        BarResetColorFade.Keyframes(from, Green, 0.35f, 0f, 0.65f, 1f)[^1].Color.A.Should().Be(255);
    }

    [TestMethod]
    public void Mix_GreyStart_TakesTheGreenHue()
    {
        var (h, _, _) = BarResetColorFade.ToHsl(BarResetColorFade.Mix(new BarResetRgba(128, 128, 128), Green, 0.5));
        var (greenHue, _, _) = BarResetColorFade.ToHsl(Green);

        h.Should().BeApproximately(greenHue, 2);
    }

    [TestMethod]
    public void Keyframes_Default_StartAtOldColourAndEndAtOpaqueGreenWithEvenTimes()
    {
        var red = new BarResetRgba(232, 17, 35);
        var frames = BarResetColorFade.Keyframes(red, Green, 0.35f, 0f, 0.65f, 1f);

        frames.Should().HaveCount(BarResetColorFade.Steps + 1);
        frames[0].Should().Be((0f, red));
        frames[^1].Should().Be((1f, Green));
        frames.Select(f => f.Time).Should().BeInAscendingOrder();
    }

    [TestMethod]
    public void Keyframes_GentleEasing_ChangesLittleAtBothEnds()
    {
        var red = new BarResetRgba(232, 17, 35);
        var frames = BarResetColorFade.Keyframes(red, Green, 0.35f, 0f, 0.65f, 1f);

        // Ease-in-out: the first and last steps move much less than the middle step
        var first = Distance(frames[0].Color, frames[1].Color);
        var middle = Distance(frames[5].Color, frames[6].Color);
        var last = Distance(frames[^2].Color, frames[^1].Color);
        first.Should().BeLessThan(middle / 2);
        last.Should().BeLessThan(middle / 2);
    }

    [TestMethod]
    [DataRow(0f, 0f)]
    [DataRow(1f, 1f)]
    [DataRow(0.5f, 0.5f)]
    public void CubicBezier_SymmetricCurve_HitsExpectedPoints(float t, float expected)
    {
        BarResetColorFade.CubicBezier(0.35f, 0f, 0.65f, 1f, t).Should().BeApproximately(expected, 0.001f);
    }

    [TestMethod]
    public void CubicBezier_Linear_IsIdentity()
    {
        BarResetColorFade.CubicBezier(0.25f, 0.25f, 0.75f, 0.75f, 0.3f).Should().BeApproximately(0.3f, 0.001f);
    }

    private static double Distance(BarResetRgba a, BarResetRgba b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    /// <summary>OKLCH lightness and chroma (test-only perceptual yardstick).</summary>
    private static (double L, double C) Oklch(BarResetRgba color)
    {
        static double Lin(byte v)
        {
            var c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        var (r, g, b) = (Lin(color.R), Lin(color.G), Lin(color.B));
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        var lightness = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        var a = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        var bb = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
        return (lightness, Math.Sqrt(a * a + bb * bb));
    }
}
