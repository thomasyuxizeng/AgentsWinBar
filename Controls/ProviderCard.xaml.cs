using AgentsWinBar.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AgentsWinBar.Controls;

/// <summary>
/// One extra usage window on a provider card (e.g. "Fable only").
/// </summary>
public sealed record CardExtraRow
{
    public string Title { get; set; } = string.Empty;
    public Helpers.WindowAccent Accent { get; set; }
    public double Percent { get; set; }
    public double TimePercent { get; set; } = -1;
    public string PaceText { get; set; } = string.Empty;
    public string PercentText { get; set; } = string.Empty;
    public string ResetText { get; set; } = string.Empty;
    public DateTime? ResetDetectedAtUtc { get; set; }
    public double? ResetFromUsedPercent { get; set; }

    /// <summary>Shared label column width of the card, so every bar ends at the same x.</summary>
    public double LabelWidth { get; set; }
}

/// <summary>
/// A card control for displaying provider usage data.
/// </summary>
public sealed partial class ProviderCard : UserControl
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(
            nameof(Data),
            typeof(UsageData),
            typeof(ProviderCard),
            new PropertyMetadata(null, OnDataChanged));

    public UsageData? Data
    {
        get => (UsageData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>
    /// The label column width shared by every card on the flyout (the widest label of any provider, see
    /// <see cref="LabelColumnWidthFor"/>), so the bars of all cards end at the same x. A card's own labels can only widen it.
    /// </summary>
    public static readonly DependencyProperty SharedLabelWidthProperty =
        DependencyProperty.Register(
            nameof(SharedLabelWidth),
            typeof(double),
            typeof(ProviderCard),
            new PropertyMetadata(0d, OnDataChanged));

    public double SharedLabelWidth
    {
        get => (double)GetValue(SharedLabelWidthProperty);
        set => SetValue(SharedLabelWidthProperty, value);
    }

    public event EventHandler<UsageData>? DetailRequested;

    private string? _iconProvider;
    private List<CardExtraRow>? _extraRows;
    private string? _lastSessionLabel;
    private string? _lastWeeklyLabel;
    private string[]? _lastExtraTitles;
    private double _lastLabelScale;
    private double _lastLabelWidth;
    private double _lastAppliedLabelWidth;

    [ThreadStatic]
    private static Dictionary<(double Scale, string Label), double>? _labelWidthCache;
    [ThreadStatic]
    private static TextBlock? _labelProbe;

    public ProviderCard()
    {
        InitializeComponent();
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ProviderCard card)
        {
            card.UpdateUI();
        }
    }

    private void OnCardClicked(object sender, PointerRoutedEventArgs e)
    {
        if (Data != null)
        {
            DetailRequested?.Invoke(this, Data);
        }
    }

    private void UpdateUI()
    {
        if (Data == null)
        {
            return;
        }

        // Provider icon (parsed once per provider: the card is updated in place on every refresh)
        if (!string.Equals(_iconProvider, Data.Provider, StringComparison.Ordinal))
        {
            ProviderIcon.Data = GetProviderIconGeometry(Data.Provider);
            _iconProvider = Data.Provider;
        }

        // Provider name
        ProviderName.Text = FormatProviderName(Data.Provider);

        CostSummary.Visibility = Data.Cost?.HasData == true ? Visibility.Visible : Visibility.Collapsed;
        CostSummary.Text = Data.Cost != null ? Helpers.CostFormatter.CardSummary(Data.Cost) : string.Empty;
        ToolTipService.SetToolTip(CostSummary, Data.Cost != null
            ? Helpers.CostFormatter.Summary(Data.Cost) + "\nList-price estimates, not billing." : null);
        var costStale = Data.Cost?.HasData == true
            ? Helpers.StaleCaption.Text(Data.Cost.IsStale, Data.Cost.LastSuccessfulFetch ?? Data.Cost.FetchedAt, DateTime.UtcNow)
            : string.Empty;
        CostStaleText.Text = costStale;
        CostStaleRow.Visibility = costStale.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(CostStaleRow, costStale.Length > 0 ? Data.Cost?.StaleReason : null);

        // Check state
        var isLoading = Data.IsLoading;
        var hasError = Data.HasError && Data.Session == null;

        // Update status indicator
        StatusNormal.Visibility = Visibility.Collapsed;
        StatusLoading.Visibility = Visibility.Collapsed;
        StatusNormal.Fill = ProviderStatusVisuals.Brush(Data.ServiceStatus);
        ToolTipService.SetToolTip(StatusNormal, Helpers.StatusDetailCaption.Tooltip(Data.ServiceStatus, DateTime.UtcNow));

        if (isLoading)
        {
            StatusLoading.Visibility = Visibility.Visible;
        }
        else
        {
            StatusNormal.Visibility = Visibility.Visible;
        }
        // Spin only while visible: a collapsed ring that stays active keeps the compositor drawing frames
        if (StatusLoading.IsActive != isLoading) StatusLoading.IsActive = isLoading;

        // Show data if available (even during loading/refresh)
        var hasSessionData = Data.Session != null;
        var hasWeeklyData = Data.Weekly != null;
        var hasAnyData = hasSessionData || hasWeeklyData;

        // Extra windows (Fable, External, ...) get their own bars under session/weekly
        var extras = hasAnyData ? Data.ExtraWindows : [];

        // Right-aligned labels share one width, so the bars of session, weekly and the extras line up, within this card
        // and (through SharedLabelWidth) with the other cards
        var labelWidth = Math.Min(MaxLabelWidth, Math.Max(LabelColumnWidth(Data.SessionLabel, Data.WeeklyLabel, extras), SharedLabelWidth));
        if (labelWidth != _lastAppliedLabelWidth)
        {
            _lastAppliedLabelWidth = labelWidth;
            SessionLabelText.MinWidth = WeeklyLabelText.MinWidth = labelWidth;
        }
        var extraRows = extras.Select(x => new CardExtraRow
        {
            Title = x.Title,
            LabelWidth = labelWidth,
            Accent = Helpers.WindowAccents.FromTitle(x.Title),
            Percent = x.Window.Percent,
            TimePercent = x.Window.ElapsedPercent ?? -1,
            PaceText = x.Window.PaceSummary,
            PercentText = x.Window.PercentText,
            ResetText = x.Window.ResetText,
            ResetDetectedAtUtc = x.Window.ResetDetectedAtUtc,
            ResetFromUsedPercent = x.Window.ResetFromUsedPercent
        }).ToList();
        // Unchanged extra rows keep their bars; only a change recreates them
        if (_extraRows == null || !_extraRows.SequenceEqual(extraRows))
        {
            _extraRows = extraRows;
            ExtraList.ItemsSource = extraRows;
        }
        ExtraList.Visibility = extras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (hasAnyData)
        {
            // Show data sections
            ErrorPanel.Visibility = Visibility.Collapsed;

            // Update labels based on provider
            SessionLabelText.Text = Data.SessionLabel;
            WeeklyLabelText.Text = Data.WeeklyLabel;

            // Session data
            if (hasSessionData)
            {
                SessionSection.Visibility = Visibility.Visible;
                SessionMeter.Percent = Data.Session!.Percent;
                SessionMeter.TimePercent = Data.Session.ElapsedPercent ?? -1;
                SessionMeter.PaceText = Data.Session.PaceSummary;
                SessionPercentText.Text = Data.Session.PercentText;
                SessionTimeText.Text = Data.Session.ResetText;
                SessionMeter.ResetFromPercent = Data.Session.ResetFromUsedPercent;
                SessionMeter.ResetDetectedAtUtc = Data.Session.ResetDetectedAtUtc;
            }
            else
            {
                SessionSection.Visibility = Visibility.Collapsed;
            }

            // Weekly data
            if (hasWeeklyData)
            {
                WeeklySection.Visibility = Visibility.Visible;
                WeeklyMeter.Percent = Data.Weekly!.Percent;
                WeeklyMeter.TimePercent = Data.Weekly.ElapsedPercent ?? -1;
                WeeklyMeter.PaceText = Data.Weekly.PaceSummary;
                WeeklyPercentText.Text = Data.Weekly.PercentText;
                WeeklyTimeText.Text = Data.Weekly.ResetText;
                WeeklyMeter.ResetFromPercent = Data.Weekly.ResetFromUsedPercent;
                WeeklyMeter.ResetDetectedAtUtc = Data.Weekly.ResetDetectedAtUtc;
            }
            else
            {
                WeeklySection.Visibility = Visibility.Collapsed;
            }
        }
        else if (hasError)
        {
            // No data and error: show error panel
            SessionSection.Visibility = Visibility.Collapsed;
            WeeklySection.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
        }
        else
        {
            // No data yet (initial loading)
            SessionSection.Visibility = Visibility.Collapsed;
            WeeklySection.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
        }
    }

    private const double MinLabelWidth = 48;
    private const double MaxLabelWidth = 150;

    /// <summary>The width of the widest label (caption style), within the card's label limits.</summary>
    private double LabelColumnWidth(string sessionLabel, string weeklyLabel, IReadOnlyList<ExtraUsageWindow> extras)
    {
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        if (_lastLabelWidth > 0 &&
            scale == _lastLabelScale &&
            string.Equals(sessionLabel, _lastSessionLabel, StringComparison.Ordinal) &&
            string.Equals(weeklyLabel, _lastWeeklyLabel, StringComparison.Ordinal) &&
            ExtraTitlesMatch(extras, _lastExtraTitles))
        {
            return _lastLabelWidth;
        }

        var result = MeasureLabelColumn(sessionLabel, weeklyLabel, extras, scale);
        _lastLabelScale = scale;
        _lastSessionLabel = sessionLabel;
        _lastWeeklyLabel = weeklyLabel;
        if (extras.Count == 0)
        {
            _lastExtraTitles = Array.Empty<string>();
        }
        else
        {
            var titles = new string[extras.Count];
            for (var i = 0; i < extras.Count; i++) titles[i] = extras[i].Title;
            _lastExtraTitles = titles;
        }
        _lastLabelWidth = result;
        return result;
    }

    /// <summary>
    /// The label column width the card for <paramref name="data"/> needs (the same labels the card shows), so the flyout
    /// can give every card the widest one. Measured once per label and scale (cached), on the UI thread.
    /// </summary>
    internal static double LabelColumnWidthFor(UsageData data, double scale)
    {
        var extras = data.Session != null || data.Weekly != null ? data.ExtraWindows : [];
        return MeasureLabelColumn(data.SessionLabel, data.WeeklyLabel, extras, scale);
    }

    private static double MeasureLabelColumn(string sessionLabel, string weeklyLabel, IReadOnlyList<ExtraUsageWindow> extras, double scale)
    {
        var style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var cache = _labelWidthCache ??= new Dictionary<(double Scale, string Label), double>();
        var widest = MinLabelWidth;
        widest = Math.Max(widest, MeasureLabel(sessionLabel, scale, style, cache));
        widest = Math.Max(widest, MeasureLabel(weeklyLabel, scale, style, cache));
        for (var i = 0; i < extras.Count; i++)
        {
            widest = Math.Max(widest, MeasureLabel(extras[i].Title, scale, style, cache));
        }
        return Math.Min(MaxLabelWidth, Math.Ceiling(widest));
    }

    private static bool ExtraTitlesMatch(IReadOnlyList<ExtraUsageWindow> extras, string[]? cached)
    {
        if (cached == null || cached.Length != extras.Count) return false;
        for (var i = 0; i < extras.Count; i++)
        {
            if (!string.Equals(extras[i].Title, cached[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static double MeasureLabel(string label, double scale, Style style, Dictionary<(double Scale, string Label), double> cache)
    {
        if (string.IsNullOrEmpty(label)) return 0;
        var key = (scale, label);
        if (cache.TryGetValue(key, out var cached)) return cached;

        _labelProbe ??= new TextBlock { TextWrapping = TextWrapping.NoWrap };
        if (!ReferenceEquals(_labelProbe.Style, style)) _labelProbe.Style = style;
        _labelProbe.Text = label;
        _labelProbe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = _labelProbe.DesiredSize.Width;

        if (cache.Count >= 256) cache.Clear();
        cache[key] = width;
        return width;
    }

    // SVG path data for provider icons (from Bootstrap Icons and UXWing)
    private static readonly Dictionary<string, string> ProviderIconPaths = new()
    {
        // Claude sparkle/starburst icon (Bootstrap Icons, viewBox 0 0 16 16)
        ["claude"] = "m3.127 10.604 3.135-1.76.053-.153-.053-.085H6.11l-.525-.032-1.791-.048-1.554-.065-1.505-.08-.38-.081L0 7.832l.036-.234.32-.214.455.04 1.009.069 1.513.105 1.097.064 1.626.17h.259l.036-.105-.089-.065-.068-.064-1.566-1.062-1.695-1.121-.887-.646-.48-.327-.243-.306-.104-.67.435-.48.585.04.15.04.593.456 1.267.981 1.654 1.218.242.202.097-.068.012-.049-.109-.181-.9-1.626-.96-1.655-.428-.686-.113-.411a2 2 0 0 1-.068-.484l.496-.674L4.446 0l.662.089.279.242.411.94.666 1.48 1.033 2.014.302.597.162.553.06.17h.105v-.097l.085-1.134.157-1.392.154-1.792.052-.504.25-.605.497-.327.387.186.319.456-.045.294-.19 1.23-.37 1.93-.243 1.29h.142l.161-.16.654-.868 1.097-1.372.484-.545.565-.601.363-.287h.686l.505.751-.226.775-.707.895-.585.759-.839 1.13-.524.904.048.072.125-.012 1.897-.403 1.024-.186 1.223-.21.553.258.06.263-.218.536-1.307.323-1.533.307-2.284.54-.028.02.032.04 1.029.098.44.024h1.077l2.005.15.525.346.315.424-.053.323-.807.411-3.631-.863-.872-.218h-.12v.073l.726.71 1.331 1.202 1.667 1.55.084.383-.214.302-.226-.032-1.464-1.101-.565-.497-1.28-1.077h-.084v.113l.295.432 1.557 2.34.08.718-.112.234-.404.141-.444-.08-.911-1.28-.94-1.44-.759-1.291-.093.053-.448 4.821-.21.246-.484.186-.403-.307-.214-.496.214-.98.258-1.28.21-1.016.19-1.263.112-.42-.008-.028-.092.012-.953 1.307-1.448 1.957-1.146 1.227-.274.109-.477-.247.045-.44.266-.39 1.586-2.018.956-1.25.617-.723-.004-.105h-.036l-4.212 2.736-.75.096-.324-.302.04-.496.154-.162 1.267-.871z",

        // OpenAI/Codex icon (Bootstrap Icons, viewBox 0 0 16 16)
        ["codex"] = "M14.949 6.547a3.94 3.94 0 0 0-.348-3.273 4.11 4.11 0 0 0-4.4-1.934A4.1 4.1 0 0 0 8.423.2 4.15 4.15 0 0 0 6.305.086a4.1 4.1 0 0 0-1.891.948 4.04 4.04 0 0 0-1.158 1.753 4.1 4.1 0 0 0-1.563.679A4 4 0 0 0 .554 4.72a3.99 3.99 0 0 0 .502 4.731 3.94 3.94 0 0 0 .346 3.274 4.11 4.11 0 0 0 4.402 1.933c.382.425.852.764 1.377.995.526.231 1.095.35 1.67.346 1.78.002 3.358-1.132 3.901-2.804a4.1 4.1 0 0 0 1.563-.68 4 4 0 0 0 1.14-1.253 3.99 3.99 0 0 0-.506-4.716m-6.097 8.406a3.05 3.05 0 0 1-1.945-.694l.096-.054 3.23-1.838a.53.53 0 0 0 .265-.455v-4.49l1.366.778q.02.011.025.035v3.722c-.003 1.653-1.361 2.992-3.037 2.996m-6.53-2.75a2.95 2.95 0 0 1-.36-2.01l.095.057L5.29 12.09a.53.53 0 0 0 .527 0l3.949-2.246v1.555a.05.05 0 0 1-.022.041L6.473 13.3c-1.454.826-3.311.335-4.15-1.098m-.85-6.94A3.02 3.02 0 0 1 3.07 3.949v3.785a.51.51 0 0 0 .262.451l3.93 2.237-1.366.779a.05.05 0 0 1-.048 0L2.585 9.342a2.98 2.98 0 0 1-1.113-4.094zm11.216 2.571L8.747 5.576l1.362-.776a.05.05 0 0 1 .048 0l3.265 1.86a3 3 0 0 1 1.173 1.207 2.96 2.96 0 0 1-.27 3.2 3.05 3.05 0 0 1-1.36.997V8.279a.52.52 0 0 0-.276-.445m1.36-2.015-.097-.057-3.226-1.855a.53.53 0 0 0-.53 0L6.249 6.153V4.598a.04.04 0 0 1 .019-.04L9.533 2.7a3.07 3.07 0 0 1 3.257.139c.474.325.843.778 1.066 1.303.223.526.289 1.103.191 1.664zM5.503 8.575 4.139 7.8a.05.05 0 0 1-.026-.037V4.049c0-.57.166-1.127.476-1.607s.752-.864 1.275-1.105a3.08 3.08 0 0 1 3.234.41l-.096.054-3.23 1.838a.53.53 0 0 0-.265.455zm.742-1.577 1.758-1 1.762 1v2l-1.755 1-1.762-1z",

        // Google Gemini icon (UXWing, viewBox 0 0 65 65, scaled to 16x16)
        ["gemini"] = "M8 0c.167 0 .313.114.354.277a9.58 9.58 0 0 0 .492 1.453c.53 1.23 1.256 2.307 2.179 3.23.923.922 2 1.649 3.23 2.179a9.59 9.59 0 0 0 1.454.492c.162.04.277.186.277.354s-.115.313-.277.354a9.58 9.58 0 0 0-1.453.492c-1.23.53-2.307 1.256-3.23 2.179-.923.922-1.649 2-2.18 3.23a9.59 9.59 0 0 0-.491 1.454.365.365 0 0 1-.354.277c-.168 0-.313-.114-.354-.277a9.58 9.58 0 0 0-.492-1.453c-.53-1.23-1.255-2.307-2.179-3.23-.922-.923-2-1.649-3.23-2.18a9.59 9.59 0 0 0-1.453-.491A.365.365 0 0 1 0 8c0-.168.114-.313.277-.354a9.58 9.58 0 0 0 1.453-.492c1.23-.53 2.308-1.256 3.23-2.179.923-.923 1.65-2 2.18-3.23a9.59 9.59 0 0 0 .491-1.453A.365.365 0 0 1 8 0z",

        // Cursor icon (simple arrow/cursor shape)
        ["cursor"] = "M1 1l13 5.5L8 8l-1.5 6z",

        // GitHub Copilot icon
        ["copilot"] = "M8 0a8 8 0 1 0 0 16A8 8 0 0 0 8 0M4.5 7.5a.5.5 0 0 1 0-1h7a.5.5 0 0 1 0 1h-7m0 3a.5.5 0 0 1 0-1h4a.5.5 0 0 1 0 1h-4"
    };

    // Default circle icon for unknown providers
    private const string DefaultIconPath = "M8 0a8 8 0 1 0 0 16A8 8 0 0 0 8 0";

    private static Geometry GetProviderIconGeometry(string provider)
    {
        var providerId = provider.ToLowerInvariant();
        var pathData = ProviderIconPaths.GetValueOrDefault(providerId, DefaultIconPath);

        try
        {
            return (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(
                typeof(Geometry), pathData);
        }
        catch
        {
            // Fallback to default circle if parsing fails
            return (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(
                typeof(Geometry), DefaultIconPath);
        }
    }

    private static string FormatProviderName(string provider) => provider.ToLowerInvariant() switch
    {
        "claude" => "Claude",
        "codex" => "Codex",
        "gemini" => "Gemini",
        "cursor" => "Cursor",
        "copilot" => "Copilot",
        _ => provider
    };
}
