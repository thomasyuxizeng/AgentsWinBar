using CodexBarWin.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CodexBarWin.Controls;

/// <summary>
/// A meter control for displaying usage percentage, with an optional underlying bar showing how much of
/// the usage window's time has passed (visible when usage is behind schedule: plenty to spare) and a marker
/// line at the same position that appears only when usage is ahead of schedule (using it too fast).
/// </summary>
public sealed partial class UsageMeter : UserControl
{
    // Soft, translucent lavender: distinct from the blue/amber/red usage colours but blends into the
    // empty track, so it reads as a quiet background hint rather than a second bar competing for attention
    private static readonly SolidColorBrush TimeBrush = new(Color.FromArgb(0x58, 0xA9, 0x9D, 0xEE));

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

    public UsageMeter()
    {
        InitializeComponent();
        TimeBar.Fill = TimeBrush;
        SizeChanged += OnSizeChanged;
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
        UpdateBar();
    }

    private void UpdateBar()
    {
        var percent = Math.Clamp(Percent, 0, 100);

        UsageBar.Width = ActualWidth * (percent / 100.0);
        UsageBar.Fill = GetBarBrush(percent, Accent);

        if (TimePercent < 0)
        {
            TimeBar.Visibility = Visibility.Collapsed;
            TimeMarker.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(this, string.IsNullOrEmpty(PaceText) ? null : PaceText);
            return;
        }

        var time = Math.Clamp(TimePercent, 0, 100);
        TimeBar.Visibility = Visibility.Visible;
        TimeBar.Width = ActualWidth * (time / 100.0);

        // The marker only appears when usage is outpacing the schedule (the blue has run past the time
        // position, so the violet layer is hidden). Otherwise the violet layer already shows the headroom.
        // Keep it fully inside the bar, even at 0% and 100%.
        TimeMarker.Visibility = percent > time && ActualWidth > TimeMarker.Width ? Visibility.Visible : Visibility.Collapsed;
        var markerX = Math.Clamp(ActualWidth * (time / 100.0) - TimeMarker.Width / 2, 0, Math.Max(0, ActualWidth - TimeMarker.Width));
        TimeMarker.Margin = new Thickness(markerX, 0, 0, 0);

        var pace = percent > time + 5 ? "ahead of time: using it fast"
            : percent < time - 5 ? "behind time: using it slowly"
            : "on pace";
        var tip = $"Used {percent:F0}% · {time:F0}% of the time has passed ({pace})";
        if (!string.IsNullOrEmpty(PaceText))
        {
            tip += Environment.NewLine + PaceText;
        }

        ToolTipService.SetToolTip(this, tip);
    }

    // Provider colours for extra windows: deliberately muted so they sit quietly next to the main blue bars
    private static readonly Color ClaudeOrange = Color.FromArgb(255, 0xC4, 0x7A, 0x5E);   // faded Anthropic orange
    private static readonly Color OpenAiTeal = Color.FromArgb(255, 0x3E, 0x9C, 0x8C);     // subtle teal

    private static Brush GetBarBrush(double percent, WindowAccent accent)
    {
        // Urgency colours are the same for every kind of bar
        if (percent >= 95) return new SolidColorBrush(Color.FromArgb(255, 232, 17, 35));   // Red - Critical
        if (percent >= 80) return new SolidColorBrush(Color.FromArgb(255, 255, 185, 0));   // Yellow - Warning

        return accent switch
        {
            WindowAccent.Claude => new SolidColorBrush(ClaudeOrange),
            WindowAccent.OpenAi or WindowAccent.Other => new SolidColorBrush(OpenAiTeal),
            WindowAccent.ClaudeAndOpenAi => ClaudeToOpenAiGradient(),
            _ => new SolidColorBrush(Color.FromArgb(255, 0, 120, 212))   // Blue - main windows
        };
    }

    /// <summary>
    /// Orange (Claude) to teal (OpenAI), for a pool shared by Claude and GPT models.
    /// </summary>
    private static LinearGradientBrush ClaudeToOpenAiGradient()
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0.5), EndPoint = new Windows.Foundation.Point(1, 0.5) };
        brush.GradientStops.Add(new GradientStop { Color = ClaudeOrange, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = OpenAiTeal, Offset = 1 });
        return brush;
    }
}
