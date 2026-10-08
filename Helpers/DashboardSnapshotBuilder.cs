using CodexBarWin.Models;
using CodexBarWin.Services;

namespace CodexBarWin.Helpers;

/// <summary>Captured state for a deterministic, UI-independent dashboard projection.</summary>
public sealed record DashboardInputs
{
    public IReadOnlyList<UsageData> Providers { get; init; } = [];
    public AppSettings Settings { get; init; } = new();
    public IReadOnlyList<UsageHistorySample> HistorySamples { get; init; } = [];
    public long HistoryFileBytes { get; init; }
    public DateTime NowUtc { get; init; }
    public DateTime? LastRefreshUtc { get; init; }
    public DateTime? NextRefreshUtc { get; init; }
    public TimeSpan? EffectiveRefreshInterval { get; init; }
    public string? CliVersion { get; init; }
    public bool? CliAvailable { get; init; }
    public string? WslDistro { get; init; }
    public string? AppVersion { get; init; }
    public string SettingsPath { get; init; } = string.Empty;
    public IReadOnlyList<DashboardRefreshEntry> RecentRefreshes { get; init; } = [];
    public IReadOnlyList<DashboardAlertEntry> RecentAlerts { get; init; } = [];
    public IReadOnlyList<DashboardCostCacheEntry> CostCache { get; init; } = [];
}

public static class DashboardSnapshotBuilder
{
    public static DashboardSnapshot Build(DashboardInputs inputs)
    {
        var settings = inputs.Settings ?? new();
        var timeouts = settings.Timeouts ?? new();
        var now = Utc(inputs.NowUtc);
        var samples = (inputs.HistorySamples ?? []).OfType<UsageHistorySample>().ToArray();
        var byProvider = samples.ToLookup(s => s.Provider, StringComparer.OrdinalIgnoreCase);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local));
        var providers = (inputs.Providers ?? []).OfType<UsageData>().Select(data =>
        {
            var id = data.Provider ?? string.Empty;
            var history = byProvider[id].ToArray();
            var extras = (data.ExtraWindows ?? []).OfType<ExtraUsageWindow>()
                .Select(e => e with { Title = e.Title ?? string.Empty }).ToArray();
            var ambiguousKeys = extras.GroupBy(UsageHistoryStore.ExtraWindowKey)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            var windows = new List<DashboardWindow>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            void Add(string key, DashboardWindowKind kind, string title, UsageWindow? window)
            {
                if (window == null) return;
                var historyKey = key;
                var suffix = 2;
                while (!keys.Add(key)) key = $"{historyKey}#{suffix++}";
                var percent = DashboardValues.Percent(window.Percent);
                var pace = double.IsFinite(window.Percent)
                    ? PaceCalculator.Project(percent, window.WindowMinutes, window.ResetAt, now)
                    : new PaceProjection(PaceKind.Unknown);
                windows.Add(new DashboardWindow
                {
                    Key = key, Kind = kind, Title = title,
                    Accent = kind is DashboardWindowKind.Session or DashboardWindowKind.Weekly
                        ? WindowAccent.None : WindowAccents.FromTitle(title),
                    UsedPercent = percent,
                    ElapsedPercent = ResetTimeFormatter.ElapsedPercent(window.ResetAt, window.WindowMinutes, now),
                    WindowMinutes = window.WindowMinutes, ResetsAtUtc = Utc(window.ResetAt),
                    ResetText = ResetTimeFormatter.Format(window.ResetAt, now) ?? window.ResetIn?.Trim() ?? string.Empty,
                    LastChangedAtUtc = Utc(window.LastChangedAt), Pace = pace,
                    PaceSummary = PaceCalculator.Summarize(pace),
                    // Duplicate source keys cannot identify which window produced a sample.
                    History = BurndownBuilder.Build(ambiguousKeys.Contains(historyKey)
                        ? [] : history.Where(s => s.WindowKey == historyKey), window, now)
                });
            }
            // Labels on UsageData require a non-null provider id.
            var labeled = data with { Provider = id };
            Add("session", DashboardWindowKind.Session, labeled.SessionLabel, data.Session);
            Add("weekly", DashboardWindowKind.Weekly, labeled.WeeklyLabel, data.Weekly);
            Add("tertiary", DashboardWindowKind.Tertiary, labeled.TertiaryLabel, data.Tertiary);
            foreach (var extra in extras)
                Add(UsageHistoryStore.ExtraWindowKey(extra), DashboardWindowKind.Extra, extra.Title, extra.Window);
            var cost = data.Cost is { } raw ? raw with { Daily = (raw.Daily ?? []).OfType<DailyCost>().ToArray() } : null;
            var oldReading = DataAge.OldReadingUtc(Utc(data.FetchedAt), data.DataUpdatedAt);
            return new DashboardProvider
            {
                Id = id, DisplayName = id.ToLowerInvariant() switch
                { "claude" => "Claude", "codex" => "Codex", "gemini" => "Gemini", _ => id },
                Plan = data.Plan, Source = data.Status, ServiceStatus = data.ServiceStatus,
                IsLoading = data.IsLoading,
                IsStale = now - Utc(data.FetchedAt) > TimeSpan.FromMinutes(5) || oldReading != null,
                Error = data.Error, FetchedAtUtc = Utc(data.FetchedAt), OldReadingUtc = oldReading, Credits = data.Credits,
                Windows = windows, Cost = settings.ShowCostTotals && cost?.HasData == true ? BuildCost(cost, today) : null,
                HistorySampleCount = history.Length, HistoryOldestUtc = Oldest(history)
            };
        }).ToArray();
        var refs = providers.SelectMany(p => p.Windows.Select(w => (Window: w, Ref: new DashboardWindowRef
        {
            ProviderId = p.Id, ProviderName = p.DisplayName, WindowTitle = w.Title,
            UsedPercent = w.UsedPercent, ResetsAtUtc = w.ResetsAtUtc
        }))).ToArray();
        var costs = providers.Select(p => p.Cost).OfType<DashboardCost>().ToArray();
        var lastRefresh = Utc(inputs.LastRefreshUtc);
        var effectiveIntervalSeconds = inputs.EffectiveRefreshInterval.HasValue
            ? (int)Math.Clamp(inputs.EffectiveRefreshInterval.Value.TotalSeconds, 0, int.MaxValue)
            : settings.RefreshIntervalSeconds;
        return new DashboardSnapshot
        {
            GeneratedAtUtc = now, Providers = providers,
            Summary = new DashboardSummary
            {
                LastRefreshUtc = lastRefresh, NextRefreshUtc = Utc(inputs.NextRefreshUtc),
                RefreshIntervalSeconds = settings.RefreshIntervalSeconds,
                EffectiveRefreshIntervalSeconds = effectiveIntervalSeconds, ProviderCount = providers.Length,
                HealthyProviderCount = providers.Count(p => string.IsNullOrEmpty(p.Error) && !p.IsStale),
                ErrorProviderCount = providers.Count(p => !string.IsNullOrEmpty(p.Error)),
                StaleProviderCount = providers.Count(p => p.IsStale),
                IncidentCount = providers.Count(p => ProviderStatusPresentation.IsIncident(p.ServiceStatus)),
                WindowCount = refs.Length, WindowsAtLimit = refs.Count(w => w.Window.UsedPercent >= 95),
                WindowsNearLimit = refs.Count(w => w.Window.UsedPercent is >= 80 and < 95),
                WindowsAheadOfPace = refs.Count(w => w.Window.Pace.Kind == PaceKind.WillRunOut),
                MostUsedWindow = refs.OrderByDescending(w => w.Window.UsedPercent).Select(w => w.Ref).FirstOrDefault(),
                NextResetWindow = refs.Where(w => w.Window.ResetsAtUtc > now)
                    .OrderBy(w => w.Window.ResetsAtUtc).Select(w => w.Ref).FirstOrDefault(),
                CostAvailable = costs.Length > 0, CostCurrency = costs.FirstOrDefault()?.Currency ?? "USD",
                TotalCostToday = costs.Sum(c => c.TodayCost), TotalCost30Days = costs.Sum(c => c.Last30DaysCost),
                TotalTokensToday = costs.Sum(c => c.TodayTokens), TotalTokens30Days = costs.Sum(c => c.Last30DaysTokens)
            },
            Diagnostics = new DashboardDiagnostics
            {
                AppVersion = inputs.AppVersion, CliVersion = inputs.CliVersion, CliAvailable = inputs.CliAvailable,
                WslDistro = inputs.WslDistro, SettingsPath = inputs.SettingsPath ?? string.Empty,
                Theme = settings.Theme.ToString(), RefreshIntervalSeconds = settings.RefreshIntervalSeconds,
                EffectiveRefreshIntervalSeconds = effectiveIntervalSeconds,
                CacheExpiryMinutes = settings.CacheExpiryMinutes, WslCommandTimeoutSeconds = timeouts.WslCommandTimeoutSeconds,
                CliProviderTimeoutSeconds = timeouts.CliProviderTimeoutSeconds,
                StandardProviderTimeoutSeconds = timeouts.StandardProviderTimeoutSeconds,
                OverlayEnabled = settings.AlwaysOnOverlayEnabled, TaskbarStripEnabled = settings.TaskbarDisplayEnabled,
                AlertsEnabled = settings.AlertsEnabled, CostTotalsEnabled = settings.ShowCostTotals,
                DeveloperModeEnabled = settings.DeveloperModeEnabled, StartWithWindows = settings.StartWithWindows,
                HistorySampleCount = samples.Length, HistoryOldestUtc = Oldest(samples),
                HistoryNewestUtc = samples.Select(s => (DateTime?)Utc(s.Timestamp)).Max(),
                HistoryFileBytes = Math.Max(0, inputs.HistoryFileBytes),
                RecentRefreshes = (inputs.RecentRefreshes ?? []).OfType<DashboardRefreshEntry>()
                    .OrderByDescending(e => Utc(e.TimestampUtc)).Take(40).ToArray(),
                RecentAlerts = (inputs.RecentAlerts ?? []).OfType<DashboardAlertEntry>()
                    .OrderByDescending(e => Utc(e.TimestampUtc)).Take(20).ToArray(),
                CostCache = (inputs.CostCache ?? []).ToArray()
            }
        };
    }

    private static DashboardCost BuildCost(CostData cost, DateOnly today)
    {
        var daily = CostSeriesBuilder.Daily(cost, today, 30);
        var active = daily.Where(d => d.TotalTokens > 0 || d.TotalCost > 0 || d.InputTokens > 0 ||
            d.OutputTokens > 0 || d.CacheReadTokens > 0 || d.CacheCreationTokens > 0 || d.ReasoningTokens > 0 ||
            d.ModelBreakdowns.Any(m => m.TotalTokens > 0 || m.Cost > 0)).ToArray();
        return new DashboardCost
        {
            Currency = cost.CurrencyCode, TodayCost = Math.Max(0, cost.TodayCostUSD), TodayTokens = Math.Max(0, cost.TodayTokens),
            Last30DaysCost = Math.Max(0, cost.Last30DaysCostUSD), Last30DaysTokens = Math.Max(0, cost.Last30DaysTokens),
            Provenance = cost.Provenance, CapturedAtUtc = Utc(cost.FetchedAt), Coverage = cost.Coverage,
            Daily = daily, TopModels = CostSeriesBuilder.TopModels(cost, today, 8),
            InputTokens = daily.Sum(d => d.InputTokens), OutputTokens = daily.Sum(d => d.OutputTokens),
            CacheReadTokens = daily.Sum(d => d.CacheReadTokens), CacheCreationTokens = daily.Sum(d => d.CacheCreationTokens),
            ReasoningTokens = daily.Sum(d => d.ReasoningTokens),
            AverageDailyCost = active.Length == 0 ? null : active.Average(d => d.TotalCost),
            PeakDay = active.OrderByDescending(d => d.TotalCost).FirstOrDefault()
        };
    }

    private static DateTime? Oldest(IEnumerable<UsageHistorySample> samples) =>
        samples.Select(s => (DateTime?)Utc(s.Timestamp)).Min();
    private static DateTime? Utc(DateTime? value) => value.HasValue ? Utc(value.Value) : null;
    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
}
