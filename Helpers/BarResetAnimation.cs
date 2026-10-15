using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace CodexBarWin.Helpers;

/// <summary>
/// The "usage window reset" effect of one bar, in this order: a bright green ring draws itself around the track,
/// the previous fill cross-fades its colour to solid green while the ring fades out, then the green fill collapses
/// to the left and hands over to a green glow that emerges from the bar's left end and sweeps the track
/// (timings in <see cref="BarResetTimeline"/>). Runs on the compositor (no layout, no timers). The ring is a child
/// visual outside the track bounds (it never affects layout); the ghost and glow are overlay rectangles that are
/// collapsed whenever idle, so the bar's resting look never changes.
/// </summary>
internal sealed class BarResetAnimation : IDisposable
{
    /// <summary>The single "reset" colour: vivid on both dark and light themes (the app has no brighter success colour).</summary>
    internal static readonly Color BrightGreen = Color.FromArgb(255, 0x28, 0xD8, 0x64);

    private const float GlowPeakAlpha = 0.9f;
    private const double GlowWidthShare = 0.38;

    private static UISettings? s_uiSettings;

    private readonly FrameworkElement _ringHost;
    private readonly FrameworkElement _clipHost;
    private readonly Rectangle _ghost;
    private readonly Rectangle _ghostGreen;
    private readonly Rectangle _glow;
    private CompositionScopedBatch? _batch;
    private ShapeVisual? _ring;
    private ShapeVisual? _fill;
    private int _generation;
    private bool _disposed;

    public BarResetAnimation(FrameworkElement ringHost, FrameworkElement clipHost, Rectangle ghost, Rectangle ghostGreen, Rectangle glow)
    {
        _ringHost = ringHost;
        _clipHost = clipHost;
        _ghost = ghost;
        _ghostGreen = ghostGreen;
        _glow = glow;
        ElementCompositionPreview.SetIsTranslationEnabled(_glow, true);
    }

    private static volatile bool s_playbackEnabled = true;

    /// <summary>
    /// The user's reset-animation setting (MainViewModel.ShouldPlayResetAnimation), kept in sync by the app. While false
    /// no bar ever creates a ring or overlay. Thread-safe; defaults to true.
    /// </summary>
    public static bool PlaybackEnabled
    {
        get => s_playbackEnabled;
        set => s_playbackEnabled = value;
    }

    /// <summary>False when the user turned Windows animations off (reduced motion).</summary>
    public static bool AnimationsEnabled
    {
        get
        {
            try { return (s_uiSettings ??= new UISettings()).AnimationsEnabled; }
            catch { return true; }
        }
    }

    /// <summary>
    /// Manual check: plays the reset animation on <paramref name="root"/> (or every usage bar below it) by setting
    /// ResetDetectedAtUtc to now, exactly as reset detection will. Call it from the UI thread on loaded elements;
    /// does nothing when Windows animations are off.
    /// </summary>
    internal static void Preview(FrameworkElement root)
    {
        if (root is Controls.UsageMeter meter)
        {
            meter.ResetDetectedAtUtc = DateTime.UtcNow;
            return;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is FrameworkElement child) Preview(child);
        }
    }

    /// <summary>
    /// Plays the effect. <paramref name="host"/> is the memory of the window that owns the bar (null for a window that does
    /// not recreate its bars); it learns that an animation is running so the window can hold its rebuild back.
    /// </summary>
    public void Play(Brush? ghostBrush, double ghostWidth, ResetHostState? host)
    {
        if (_disposed) return;
        var width = _clipHost.ActualWidth;
        var height = _clipHost.ActualHeight;
        if (width <= 0 || height <= 0) return;

        // A window that rebuilds its bars waits for the animation to finish (see the strip and the overlay)
        host?.MarkBusy(Environment.TickCount64, BarResetTimeline.TotalMs + 150);
        Stop();
        var generation = _generation;

        var ghostVisual = ElementCompositionPreview.GetElementVisual(_ghost);
        var greenVisual = ElementCompositionPreview.GetElementVisual(_ghostGreen);
        var glowVisual = ElementCompositionPreview.GetElementVisual(_glow);
        var compositor = ghostVisual.Compositor;
        var gentle = compositor.CreateCubicBezierEasingFunction(
            new Vector2(BarResetTimeline.GentleX1, BarResetTimeline.GentleY1),
            new Vector2(BarResetTimeline.GentleX2, BarResetTimeline.GentleY2));
        var sweepEase = compositor.CreateCubicBezierEasingFunction(
            new Vector2(BarResetTimeline.SweepX1, BarResetTimeline.SweepY1),
            new Vector2(BarResetTimeline.SweepX2, BarResetTimeline.SweepY2));
        var linear = compositor.CreateLinearEasingFunction();
        var easeInOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));
        var green = BrightGreen;
        var total = TimeSpan.FromMilliseconds(BarResetTimeline.TotalMs);

        // Clips the ghost and glow to the track; the ring is hosted on the (unclipped) control itself, not on this layer or the rounded RootGrid
        _clipHost.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, height) };

        // Ring: pill-shaped child visual around the track (see BarResetRingGeometry). A ShapeVisual clips its shapes
        // to its size, so the visual is padded beyond the ring's outer edge. Starts from a clean state (nothing
        // drawn, invisible) before it is attached.
        var layout = BarResetRingGeometry.For(width, height);
        var ringVisual = compositor.CreateShapeVisual();
        ringVisual.Offset = new Vector3(layout.VisualOffset, 0f);
        ringVisual.Size = layout.VisualSize;
        ringVisual.Opacity = 0f;
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.Offset = layout.GeometryOffset;
        geometry.Size = layout.GeometrySize;
        geometry.CornerRadius = new Vector2(layout.CornerRadius, layout.CornerRadius);
        geometry.TrimStart = 0f;
        geometry.TrimEnd = 0f;
        geometry.TrimOffset = 0f;   // path starts where the top edge leaves the left end and runs clockwise
        // Two layers on the SAME geometry (so they can never drift apart), bottom to top:
        // - feather: same path, 0.5 px wider on each side, faint, so the ring's edges are not razor sharp. Flat
        //   caps, so it never overlaps itself (no brighter spot where the ring closes)
        // - main: the opaque stroke, with round caps so its start and head are rounded (no notch); it is opaque,
        //   so where its caps overlap at the closing point nothing shows
        var feather = compositor.CreateSpriteShape(geometry);
        feather.StrokeBrush = compositor.CreateColorBrush(WithAlpha(green, BarResetRingGeometry.FeatherOpacity));
        feather.StrokeThickness = layout.FeatherThickness;
        feather.StrokeStartCap = CompositionStrokeCap.Flat;
        feather.StrokeEndCap = CompositionStrokeCap.Flat;

        var shape = compositor.CreateSpriteShape(geometry);
        shape.StrokeBrush = compositor.CreateColorBrush(green);
        shape.StrokeThickness = layout.StrokeThickness;
        shape.StrokeStartCap = CompositionStrokeCap.Round;
        shape.StrokeEndCap = CompositionStrokeCap.Round;

        ringVisual.Shapes.Add(feather);
        ringVisual.Shapes.Add(shape);
        _ring = ringVisual;

        // Fill: the previous fill at its old width. For solid and linear-gradient brushes (all bar brushes) it is
        // redrawn as a composition shape on the green ghost, whose colours fade to green through clean hues
        // (BarResetColorFade). Any other brush falls back to an opaque cross-fade: the old fill with solid green
        // fading in over it.
        var showGhost = ghostBrush != null && ghostWidth >= 1;
        var fadeStops = showGhost ? FadeStops(ghostBrush) : null;
        var colourTargets = new List<(CompositionObject Target, Color From)>();
        if (showGhost)
        {
            var w = Math.Min(ghostWidth, width);
            _ghostGreen.Width = w;
            _ghostGreen.Visibility = Visibility.Visible;
            greenVisual.CenterPoint = Vector3.Zero;
            if (fadeStops != null)
            {
                _ghostGreen.Fill = null;
                greenVisual.Opacity = 1f;
                _fill = CreateFillVisual(compositor, (float)w, (float)height, fadeStops.Value, colourTargets);
                ElementCompositionPreview.SetElementChildVisual(_ghostGreen, _fill);
            }
            else
            {
                _ghost.Fill = ghostBrush;
                _ghost.Width = w;
                _ghost.Visibility = Visibility.Visible;
                _ghostGreen.Fill = new SolidColorBrush(green);
                ghostVisual.Opacity = 1f;
                greenVisual.Opacity = 0f;
            }
        }

        // Wide soft band with a bright flat core
        var glowWidth = Math.Max(24, width * GlowWidthShare);
        _glow.Width = glowWidth;
        _glow.Fill = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0.5),
            EndPoint = new Windows.Foundation.Point(1, 0.5),
            GradientStops =
            {
                new GradientStop { Color = GlowColor(green, 0f), Offset = 0 },
                new GradientStop { Color = GlowColor(green, 0.35f), Offset = 0.25 },
                new GradientStop { Color = GlowColor(green, 1f), Offset = 0.45 },
                new GradientStop { Color = GlowColor(green, 1f), Offset = 0.55 },
                new GradientStop { Color = GlowColor(green, 0.35f), Offset = 0.75 },
                new GradientStop { Color = GlowColor(green, 0f), Offset = 1 }
            }
        };
        _glow.Visibility = Visibility.Visible;
        glowVisual.Opacity = 0f;
        var glowStartX = (float)(-glowWidth / 2);   // core on the collapse end point (the track's left end)
        glowVisual.Properties.InsertVector3("Translation", new Vector3(glowStartX, 0, 0));

        _batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        // a. Ring draws itself with a gentle ease-in-out
        var trim = compositor.CreateScalarKeyFrameAnimation();
        trim.InsertKeyFrame(0f, 0f);
        trim.InsertKeyFrame(1f, 1f, gentle);
        trim.Duration = TimeSpan.FromMilliseconds(BarResetTimeline.RingMs);
        geometry.StartAnimation("TrimEnd", trim);

        // The ring is hidden until its stroke has some length (no dot at the start point), then fully opaque
        // until c, where it fades out together with the colour fill
        var ringFade = compositor.CreateScalarKeyFrameAnimation();
        ringFade.InsertKeyFrame(0f, 0f);
        ringFade.InsertKeyFrame(BarResetTimeline.Fraction(BarResetTimeline.RingRevealMs), 1f, linear);
        ringFade.InsertKeyFrame(BarResetTimeline.Fraction(BarResetTimeline.RingFadeStartMs), 1f);
        ringFade.InsertKeyFrame(BarResetTimeline.Fraction(BarResetTimeline.RingFadeEndMs), 0f, easeInOut);
        ringFade.Duration = total;
        ringVisual.StartAnimation("Opacity", ringFade);

        // Attach only now: the first visible frame is the clean state (TrimEnd 0), animations already running
        ElementCompositionPreview.SetElementChildVisual(_ringHost, ringVisual);

        if (showGhost && fadeStops != null)
        {
            // b. Each colour of the old fill fades to the ring's green through clean hues (HSL keyframes computed
            // with the gentle curve, blended linearly between them), ending fully opaque
            var to = ToRgba(green);
            foreach (var (target, from) in colourTargets)
            {
                var fade = compositor.CreateColorKeyFrameAnimation();
                fade.InterpolationColorSpace = CompositionColorSpace.Rgb;
                fade.InsertKeyFrame(0f, from);
                var frames = BarResetColorFade.Keyframes(ToRgba(from), to,
                    BarResetTimeline.GentleX1, BarResetTimeline.GentleY1, BarResetTimeline.GentleX2, BarResetTimeline.GentleY2);
                foreach (var (time, colour) in frames)
                {
                    var ms = (int)Math.Round(BarResetTimeline.FillStartMs + time * BarResetTimeline.FillMs);
                    fade.InsertKeyFrame(BarResetTimeline.Fraction(ms), ToColor(colour), linear);
                }
                fade.Duration = total;
                target.StartAnimation("Color", fade);
            }

            // d. Opaque until its last tenth (so the end is not a hard pop) while it collapses to the left
            var greenFade = compositor.CreateScalarKeyFrameAnimation();
            greenFade.InsertKeyFrame(0f, 1f);
            greenFade.InsertKeyFrame(BarResetTimeline.Fraction(BarResetTimeline.CollapseFadeStartMs), 1f);
            greenFade.InsertKeyFrame(BarResetTimeline.Fraction(BarResetTimeline.CollapseEndMs), 0f, linear);
            greenFade.Duration = total;
            greenVisual.StartAnimation("Opacity", greenFade);
        }
        else if (showGhost)
        {
            // b. Fallback: solid green fades in over the opaque old fill, ending fully opaque
            // d. then, opaque until its last tenth (so the end is not a hard pop), it collapses to the left
            var greenFade = compositor.CreateScalarKeyFrameAnimation();
            greenFade.InsertKeyFrame(0f, 0f);
            greenFade.InsertKeyFrame(BarResetTimeline.Fraction(BarResetTimeline.FillStartMs), 0f);
            greenFade.InsertKeyFrame(BarResetTimeline.Fraction(BarResetTimeline.FillEndMs), 1f, gentle);
            greenFade.InsertKeyFrame(BarResetTimeline.Fraction(BarResetTimeline.CollapseFadeStartMs), 1f);
            greenFade.InsertKeyFrame(BarResetTimeline.Fraction(BarResetTimeline.CollapseEndMs), 0f, linear);
            greenFade.Duration = total;
            greenVisual.StartAnimation("Opacity", greenFade);

            // The old colour is dropped in the first frame after the fill, while the opaque green still covers it
            var oldHide = compositor.CreateScalarKeyFrameAnimation();
            oldHide.InsertKeyFrame(0f, 1f);
            oldHide.InsertKeyFrame(BarResetTimeline.Fraction(BarResetTimeline.FillEndMs), 1f);
            oldHide.InsertKeyFrame(BarResetTimeline.Fraction(BarResetTimeline.FillEndMs + 16), 0f, linear);
            oldHide.Duration = total;
            ghostVisual.StartAnimation("Opacity", oldHide);
        }

        if (showGhost)
        {
            // d. Collapse to the left (gentle ease-in-out: lands softly, at zero speed, where the sweep picks up)
            var collapse = compositor.CreateScalarKeyFrameAnimation();
            collapse.InsertKeyFrame(0f, 1f);
            collapse.InsertKeyFrame(1f, 0f, gentle);
            collapse.Duration = TimeSpan.FromMilliseconds(BarResetTimeline.CollapseMs);
            collapse.DelayTime = TimeSpan.FromMilliseconds(BarResetTimeline.CollapseStartMs);
            collapse.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            greenVisual.StartAnimation("Scale.X", collapse);
        }

        // e. Handoff: the glow starts just before the collapse ends, with its bright core centred on the bar's left
        // end where the green has collapsed to. It fades in while the green's last sliver fades out, starts from zero
        // speed like the collapse ends (no jump), sweeps past the right end and fades out softly
        var sweep = compositor.CreateScalarKeyFrameAnimation();
        sweep.InsertKeyFrame(0f, glowStartX);
        sweep.InsertKeyFrame(1f, (float)width, sweepEase);
        sweep.Duration = TimeSpan.FromMilliseconds(BarResetTimeline.SweepMs);
        sweep.DelayTime = TimeSpan.FromMilliseconds(BarResetTimeline.SweepStartMs);
        sweep.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        glowVisual.StartAnimation("Translation.X", sweep);

        var glowFade = compositor.CreateScalarKeyFrameAnimation();
        glowFade.InsertKeyFrame(0f, 0f);
        glowFade.InsertKeyFrame(BarResetTimeline.GlowRevealMs / (float)BarResetTimeline.SweepMs, 1f, easeInOut);
        glowFade.InsertKeyFrame(1f - BarResetTimeline.SweepFadeShare, 1f);
        glowFade.InsertKeyFrame(1f, 0f, easeInOut);
        glowFade.Duration = TimeSpan.FromMilliseconds(BarResetTimeline.SweepMs);
        glowFade.DelayTime = TimeSpan.FromMilliseconds(BarResetTimeline.SweepStartMs);
        glowFade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        glowVisual.StartAnimation("Opacity", glowFade);

        var batch = _batch;
        batch.Completed += (_, _) =>
        {
            // A restart or Stop bumps the generation; a stale batch must not tear down the newer run
            if (generation == _generation && !_disposed) Stop();
        };
        batch.End();
    }

    /// <summary>Cancels any running effect and returns everything to its idle (collapsed, empty) state.</summary>
    public void Stop()
    {
        _generation++;
        _batch = null;

        var ghostVisual = ElementCompositionPreview.GetElementVisual(_ghost);
        var greenVisual = ElementCompositionPreview.GetElementVisual(_ghostGreen);
        var glowVisual = ElementCompositionPreview.GetElementVisual(_glow);
        ghostVisual.StopAnimation("Opacity");
        greenVisual.StopAnimation("Opacity");
        greenVisual.StopAnimation("Scale.X");
        glowVisual.StopAnimation("Translation.X");
        glowVisual.StopAnimation("Opacity");
        ghostVisual.Opacity = 1f;
        greenVisual.Opacity = 1f;
        greenVisual.Scale = Vector3.One;
        glowVisual.Opacity = 1f;

        if (_ring != null)
        {
            _ring.StopAnimation("Opacity");
            ElementCompositionPreview.SetElementChildVisual(_ringHost, null);
            _ring.Dispose();
            _ring = null;
        }

        if (_fill != null)
        {
            ElementCompositionPreview.SetElementChildVisual(_ghostGreen, null);
            _fill.Dispose();
            _fill = null;
        }

        _ghost.Visibility = Visibility.Collapsed;
        _ghostGreen.Visibility = Visibility.Collapsed;
        _glow.Visibility = Visibility.Collapsed;
        _ghost.Fill = null;
        _ghostGreen.Fill = null;
        _glow.Fill = null;
        _clipHost.Clip = null;
    }

    private static Color GlowColor(Color green, float share) => WithAlpha(green, GlowPeakAlpha * share);

    private static Color WithAlpha(Color color, float alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255), color.R, color.G, color.B);

    private static BarResetRgba ToRgba(Color c) => new(c.R, c.G, c.B, c.A);

    private static Color ToColor(BarResetRgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>Colours of the previous fill brush (alpha includes the brush opacity), or null if it is neither solid nor a relative linear gradient.</summary>
    private static FillStops? FadeStops(Brush? brush)
    {
        static Color Scaled(Color c, double opacity) => Color.FromArgb((byte)Math.Round(c.A * Math.Clamp(opacity, 0, 1)), c.R, c.G, c.B);

        switch (brush)
        {
            case SolidColorBrush solid:
                return new FillStops(new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), [(0f, Scaled(solid.Color, solid.Opacity))]);
            case LinearGradientBrush gradient when gradient.MappingMode == BrushMappingMode.RelativeToBoundingBox && gradient.GradientStops.Count > 0:
                var stops = gradient.GradientStops.Select(s => ((float)s.Offset, Scaled(s.Color, gradient.Opacity))).ToArray();
                return new FillStops(
                    new Vector2((float)gradient.StartPoint.X, (float)gradient.StartPoint.Y),
                    new Vector2((float)gradient.EndPoint.X, (float)gradient.EndPoint.Y),
                    stops);
            default:
                return null;
        }
    }

    /// <summary>
    /// The previous fill as a rounded composition shape (corner radii clamped like the XAML rectangles'), with one
    /// animatable colour target per colour (the solid brush, or each gradient stop).
    /// </summary>
    private static ShapeVisual CreateFillVisual(Compositor compositor, float width, float height, FillStops fill, List<(CompositionObject Target, Color From)> targets)
    {
        CompositionBrush brush;
        if (fill.Stops.Length == 1)
        {
            var solid = compositor.CreateColorBrush(fill.Stops[0].Color);
            targets.Add((solid, fill.Stops[0].Color));
            brush = solid;
        }
        else
        {
            var gradient = compositor.CreateLinearGradientBrush();
            gradient.StartPoint = fill.Start;
            gradient.EndPoint = fill.End;
            foreach (var (offset, color) in fill.Stops)
            {
                var stop = compositor.CreateColorGradientStop(offset, color);
                gradient.ColorStops.Add(stop);
                targets.Add((stop, color));
            }
            brush = gradient;
        }

        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new Vector2(width, height);
        geometry.CornerRadius = new Vector2(
            Math.Min(BarResetRingGeometry.TrackCornerRadius, width / 2),
            Math.Min(BarResetRingGeometry.TrackCornerRadius, height / 2));
        var shape = compositor.CreateSpriteShape(geometry);
        shape.FillBrush = brush;

        var visual = compositor.CreateShapeVisual();
        visual.Size = new Vector2(width, height);
        visual.Shapes.Add(shape);
        return visual;
    }

    private readonly record struct FillStops(Vector2 Start, Vector2 End, (float Offset, Color Color)[] Stops);

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
