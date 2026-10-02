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
        UsageBar.Fill = GetBarColor(percent);

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

    private static SolidColorBrush GetBarColor(double percent) => percent switch
    {
        >= 95 => new SolidColorBrush(Color.FromArgb(255, 232, 17, 35)),   // Red - Critical
        >= 80 => new SolidColorBrush(Color.FromArgb(255, 255, 185, 0)),   // Yellow - Warning
        _ => new SolidColorBrush(Color.FromArgb(255, 0, 120, 212))        // Blue - Normal
    };
}
