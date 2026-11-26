using AgentsWinBar.Helpers;
using AgentsWinBar.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AgentsWinBar.Controls;

/// <summary>
/// A meter control for displaying usage percentage, with an optional underlying bar showing how much of
/// the usage window's time has passed (visible when usage is behind schedule: plenty to spare) and a marker
/// line at the same position that appears only when usage is ahead of schedule (using it too fast).
/// </summary>
public sealed partial class UsageMeter : UserControl
{
    // Soft, translucent lavender for the time-passed layer of the main bars: distinct from the blue/amber/red
    // usage colours but blends into the empty track, so it reads as a quiet background hint
    private static readonly Color MainTimeColor = Color.FromArgb(0x58, 0xA9, 0x9D, 0xEE);

    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(
            nameof(Percent),
            typeof(double),
            typeof(UsageMeter),
            new PropertyMetadata(0.0, OnPercentChanged));

    /// <summary>
    /// How much of the usage window's time has elapsed (0-100). A negative value means unknown (layer hidden).
    /// </summary>
    public static readonly DependencyProperty TimePercentProperty =
        DependencyProperty.Register(
            nameof(TimePercent),
            typeof(double),
            typeof(UsageMeter),
            new PropertyMetadata(-1.0, OnPercentChanged));

    /// <summary>
    /// Optional extra tooltip line, e.g. the projected run-out time ("At this pace you'll hit the limit in ...").
    /// </summary>
    public static readonly DependencyProperty PaceTextProperty =
        DependencyProperty.Register(
            nameof(PaceText),
            typeof(string),
            typeof(UsageMeter),
            new PropertyMetadata(string.Empty, OnPercentChanged));

    public string PaceText
    {
        get => (string)GetValue(PaceTextProperty);
        set => SetValue(PaceTextProperty, value);
    }

    /// <summary>
    /// Which provider's models the bar is for. None (the default) is a main session/weekly window and uses
    /// the standard blue; extra windows use a provider colour (Claude: faded Anthropic orange, OpenAI: teal,
    /// both: an orange to teal gradient) so the two kinds can be told apart. Warning colours (amber, red)
    /// are the same for every kind because urgency matters more.
    /// </summary>
    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(
            nameof(Accent),
            typeof(WindowAccent),
            typeof(UsageMeter),
            new PropertyMetadata(WindowAccent.None, OnPercentChanged));

    public WindowAccent Accent
    {
        get => (WindowAccent)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public double TimePercent
    {
        get => (double)GetValue(TimePercentProperty);
        set => SetValue(TimePercentProperty, value);
    }

    /// <summary>
    /// When a reset of this bar's usage window was detected (UTC). A new value that is recent (see
    /// <see cref="BarResetAnimationPolicy"/>) plays the reset animation once; old, unchanged or null values and
    /// unloaded/hidden bars do nothing.
    /// </summary>
    public static readonly DependencyProperty ResetDetectedAtUtcProperty =
        DependencyProperty.Register(
            nameof(ResetDetectedAtUtc),
            typeof(DateTime?),
            typeof(UsageMeter),
            new PropertyMetadata(null, OnResetDetectedAtUtcChanged));

    public DateTime? ResetDetectedAtUtc
    {
        get => (DateTime?)GetValue(ResetDetectedAtUtcProperty);
        set => SetValue(ResetDetectedAtUtcProperty, value);
    }

    /// <summary>
    /// The used percent just before the reset in <see cref="ResetDetectedAtUtc"/>. Set it BEFORE the timestamp: it is the
    /// source of the old fill the animation turns green. Null falls back to this meter's own memory of its previous fill.
    /// </summary>
    public static readonly DependencyProperty ResetFromPercentProperty =
        DependencyProperty.Register(
            nameof(ResetFromPercent),
            typeof(double?),
            typeof(UsageMeter),
            new PropertyMetadata(null));

    public double? ResetFromPercent
    {
        get => (double?)GetValue(ResetFromPercentProperty);
        set => SetValue(ResetFromPercentProperty, value);
    }

    /// <summary>
    /// Identifies this bar in a window that recreates its bars (the taskbar strip and the overlay): a reset the window has
    /// already played for this key is not played again by the recreated bar. Null (the default) keeps the bar's own memory only.
    /// </summary>
    public static readonly DependencyProperty ResetKeyProperty =
        DependencyProperty.Register(nameof(ResetKey), typeof(string), typeof(UsageMeter), new PropertyMetadata(null));

    public string? ResetKey
    {
        get => (string?)GetValue(ResetKeyProperty);
        set => SetValue(ResetKeyProperty, value);
    }

    /// <summary>
    /// What the window that recreates its bars remembers about reset animations (see <see cref="ResetKey"/>). The window owns
    /// it and hands it to every bar it creates; a lookup through <c>XamlRoot</c> would not do, because that projected object
    /// is recreated after a garbage collection and the memory would be lost with it. Set it BEFORE the timestamp; null
    /// (the default) keeps the bar's own memory only.
    /// </summary>
    public static readonly DependencyProperty ResetHostProperty =
        DependencyProperty.Register(nameof(ResetHost), typeof(ResetHostState), typeof(UsageMeter), new PropertyMetadata(null));

    public ResetHostState? ResetHost
    {
        get => (ResetHostState?)GetValue(ResetHostProperty);
        set => SetValue(ResetHostProperty, value);
    }

    private BarResetAnimation? _resetAnimation;
    private IDisposable? _resumedSubscription;
    private bool _disposed;
    private double _fillWidth;
    private double _previousFillWidth;
    private Brush? _previousFillBrush;

    public UsageMeter()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        _resumedSubscription?.Dispose();
        _resumedSubscription = ResetAnimationVisibility.Shared.SubscribeResumed(this, static meter => meter.OnScreenVisibleAgain());
        PlayPendingReset();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _resumedSubscription?.Dispose();
        _resumedSubscription = null;
        _resetAnimation?.Stop();
    }

    /// <summary>Detaches event subscriptions, stops any active reset animation, and releases brushes when the host window closes.</summary>
    public void Cleanup()
    {
        if (_disposed) return;
        _disposed = true;
        SizeChanged -= OnSizeChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _resumedSubscription?.Dispose();
        _resumedSubscription = null;
        _resetAnimation?.Dispose();
        _resetAnimation = null;
        _previousFillBrush = null;
        _barBrushes.Clear();
        _timeBrushes.Clear();
        UsageBar.Fill = null;
        TimeBar.Fill = null;
        BackgroundRect.Fill = null;
        TimeMarker.Fill = null;
        ToolTipService.SetToolTip(this, null);
        DataContext = null;
    }

    // A reset detected while the session was locked waits here; the session coming back plays it once
    private void OnScreenVisibleAgain()
    {
        if (_disposed) return;
        DispatcherQueue?.TryEnqueue(() => { if (!_disposed) PlayPendingReset(); });
    }

    // The last timestamp this bar played, so a persistent bar that is re-loaded (page navigation) never replays it
    private DateTime? _playedResetAt;

    private static void OnResetDetectedAtUtcChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UsageMeter meter) return;
        meter.TryPlayReset((DateTime?)e.NewValue, (DateTime?)e.OldValue);
    }

    // Bars in rebuilt row templates get their timestamp before they are loaded, so a fresh one waits for Loaded / first size
    private void PlayPendingReset()
    {
        if (ResetDetectedAtUtc is { } value && value != _playedResetAt) TryPlayReset(value, null);
    }

    private void TryPlayReset(DateTime? newValue, DateTime? oldValue)
    {
        if (_disposed) return;
        // Nothing plays on a locked screen: it would be missed. The reset is remembered (see MainViewModel) and plays on return.
        var shown = IsLoaded && Visibility == Visibility.Visible && ActualWidth > 0 && !SessionStateNative.IsCurrentSessionLocked();
        var visibleSince = newValue is { } stamp ? ResetAnimationVisibility.Shared.VisibleSince(stamp) : null;
        if (!BarResetAnimationPolicy.ShouldPlay(newValue, oldValue, DateTime.UtcNow, BarResetAnimation.AnimationsEnabled, shown, BarResetAnimation.PlaybackEnabled, visibleSince)) return;
        _playedResetAt = newValue;
        // A window that rebuilds its bars gives each a new instance: the window remembers what it already played
        if (ResetKey is { } key && newValue is { } playedAt && ResetHost is { } host && !host.TryMarkPlayed(key, playedAt)) return;
        PlayResetAnimation();
    }

    private void PlayResetAnimation()
    {
        try
        {
            _resetAnimation ??= new BarResetAnimation(this, ResetLayer, ResetGhost, ResetGhostGreen, ResetGlow);
            // The usage may already have dropped to empty before this timestamp arrived, so fall back to the previous fill
            if (BarResetGhost.ColourPercent(ResetFromPercent) is { } fromPercent && BarResetGhost.Width(ActualWidth, ResetFromPercent) is { } fromWidth)
            {
                _resetAnimation.Play(BarBrushFor(fromPercent), fromWidth, ResetHost);
                return;
            }

            var useCurrent = _fillWidth >= 1;
            _resetAnimation.Play(useCurrent ? UsageBar.Fill : _previousFillBrush, useCurrent ? _fillWidth : _previousFillWidth, ResetHost);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<UsageMeter>>().LogError(ex, "Usage meter reset animation failed");
        }
    }

    private static void OnPercentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UsageMeter meter)
        {
            meter.UpdateBar();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            UpdateBar();
            PlayPendingReset();
        }
        catch (Exception ex) { App.Services.GetRequiredService<ILogger<UsageMeter>>().LogError(ex, "Usage meter size update failed"); }
    }

    private (double Percent, double Time, WindowAccent Accent, string Pace, double Width)? _lastUpdate;
    private string? _tooltip;
    private readonly Dictionary<(WindowAccent Accent, int Band), Brush> _barBrushes = new();
    private readonly Dictionary<WindowAccent, Brush> _timeBrushes = new();

    private void SetTooltip(string? text)
    {
        if (_tooltip == text) return;
        _tooltip = text;
        ToolTipService.SetToolTip(this, text);
    }

    private Brush BarBrushFor(double honestPercent)
    {
        var key = (Accent, UsageThresholds.Level(honestPercent));
        if (Accent == WindowAccent.ClaudeAndOpenAi && honestPercent < UsageThresholds.Warning)
            return ClaudeToOpenAiGradient();
        if (!_barBrushes.TryGetValue(key, out var barBrush))
            _barBrushes[key] = barBrush = CreateBarBrush(honestPercent, Accent);
        return barBrush;
    }

    private void UpdateBar()
    {
        if (_disposed) return;
        var signature = (Percent, TimePercent, Accent, PaceText, ActualWidth);
        if (_lastUpdate == signature) return;
        _lastUpdate = signature;
        var visualPercent = DashboardValues.Percent(Percent);
        var honestPercent = double.IsFinite(Percent) ? Math.Max(0, Percent) : 0;

        var newFill = ActualWidth * (visualPercent / 100.0);
        if (Math.Abs(newFill - _fillWidth) > 0.5)
        {
            _previousFillWidth = _fillWidth;
            _previousFillBrush = UsageBar.Fill;
        }
        _fillWidth = newFill;
        UsageBar.Width = newFill;
        var barBrush = BarBrushFor(honestPercent);
        if (!ReferenceEquals(UsageBar.Fill, barBrush)) UsageBar.Fill = barBrush;

        if (!double.IsFinite(TimePercent) || TimePercent < 0)
        {
            TimeBar.Visibility = Visibility.Collapsed;
            TimeMarker.Visibility = Visibility.Collapsed;
            SetTooltip(string.IsNullOrEmpty(PaceText) ? null : PaceText);
            return;
        }

        var time = Math.Clamp(TimePercent, 0, 100);
        TimeBar.Visibility = Visibility.Visible;
        TimeBar.Width = ActualWidth * (time / 100.0);
        var timeBrush = TimeBrushFor(Accent);
        if (!ReferenceEquals(TimeBar.Fill, timeBrush)) TimeBar.Fill = timeBrush;

        // The marker only appears when usage is outpacing the schedule (the blue has run past the time
        // position, so the violet layer is hidden). Otherwise the violet layer already shows the headroom.
        // Keep it fully inside the bar, even at 0% and 100%.
        TimeMarker.Visibility = honestPercent > time && ActualWidth > TimeMarker.Width ? Visibility.Visible : Visibility.Collapsed;
        var markerX = Math.Clamp(ActualWidth * (time / 100.0) - TimeMarker.Width / 2, 0, Math.Max(0, ActualWidth - TimeMarker.Width));
        TimeMarker.Margin = new Thickness(markerX, 0, 0, 0);

        var pace = honestPercent > time + 5 ? "ahead of time: using it fast"
            : honestPercent < time - 5 ? "behind time: using it slowly"
            : "on pace";
        var tip = $"Used {honestPercent:F0}% · {time:F0}% of the time has passed ({pace})";
        if (!string.IsNullOrEmpty(PaceText))
        {
            tip += Environment.NewLine + PaceText;
        }

        SetTooltip(tip);
    }

    // Provider colours for extra windows
    private static readonly Color ClaudeOrange = Color.FromArgb(255, 0xD9, 0x77, 0x57);   // Anthropic orange (#D97757)
    private static readonly Color OpenAiTeal = Color.FromArgb(255, 0x10, 0xA3, 0x7F);     // OpenAI green (#10A37F)

    private Brush TimeBrushFor(WindowAccent accent)
    {
        if (accent == WindowAccent.ClaudeAndOpenAi) return ClaudeToOpenAiGradient(0x88);
        if (!_timeBrushes.TryGetValue(accent, out var brush)) _timeBrushes[accent] = brush = CreateTimeBrush(accent);
        return brush;
    }

    // Brushes are owned per-control (never cached statically across windows, since Brush is a DependencyObject
    // tied to its visual tree / compositor).
    internal static Brush GetBarBrush(double percent, WindowAccent accent) => CreateBarBrush(percent, accent);

    private static Brush CreateBarBrush(double percent, WindowAccent accent)
    {
        // Urgency colours are the same for every kind of bar
        if (percent >= UsageThresholds.Critical) return new SolidColorBrush(Color.FromArgb(255, 232, 17, 35));   // Red - Critical
        if (percent >= UsageThresholds.Warning) return new SolidColorBrush(Color.FromArgb(255, 255, 185, 0));   // Yellow - Warning

        return accent switch
        {
            WindowAccent.Claude => new SolidColorBrush(ClaudeOrange),
            WindowAccent.OpenAi or WindowAccent.Other => new SolidColorBrush(OpenAiTeal),
            WindowAccent.ClaudeAndOpenAi => ClaudeToOpenAiGradient(),
            _ => new SolidColorBrush(Color.FromArgb(255, 0, 120, 212))   // Blue - main windows
        };
    }

    /// <summary>
    /// The translucent time-passed layer: lavender on the main bars, and a faded version of the provider's
    /// colour on the extra bars, so e.g. a Fable bar looks orange even before any usage fills it.
    /// </summary>
    internal static Brush GetTimeBrush(WindowAccent accent) => CreateTimeBrush(accent);

    private static Brush CreateTimeBrush(WindowAccent accent)
    {
        const byte alpha = 0x88;
        return accent switch
        {
            WindowAccent.Claude => new SolidColorBrush(WithAlpha(ClaudeOrange, alpha)),
            WindowAccent.OpenAi or WindowAccent.Other => new SolidColorBrush(WithAlpha(OpenAiTeal, alpha)),
            WindowAccent.ClaudeAndOpenAi => ClaudeToOpenAiGradient(alpha),
            _ => new SolidColorBrush(MainTimeColor)
        };
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>
    /// Orange (Claude) to teal (OpenAI), for a pool shared by Claude and GPT models. Orange and teal are
    /// near-opposites, so blending them directly passes through muddy brown-grey. The gradient is steered
    /// around that by bridging colours that follow the hue wheel the long way (orange, gold, fresh green,
    /// OpenAI green) at a matching richness, so the whole bar stays clean.
    /// </summary>
    private static LinearGradientBrush ClaudeToOpenAiGradient(byte alpha = 255)
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0.5), EndPoint = new Windows.Foundation.Point(1, 0.5) };
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(ClaudeOrange, alpha), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(Color.FromArgb(255, 0xD2, 0xA2, 0x4C), alpha), Offset = 0.34 });   // gold
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(Color.FromArgb(255, 0x4D, 0xAE, 0x75), alpha), Offset = 0.67 });   // fresh green
        brush.GradientStops.Add(new GradientStop { Color = WithAlpha(OpenAiTeal, alpha), Offset = 1 });
        return brush;
    }
}
