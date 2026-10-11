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
    public ICostLedger? CostLedger { get; init; }
    public IEarlyResetStore? EarlyResetStore { get; init; }
    public IReadOnlyList<EarlyReset>? EarlyResets { get; init; }
}

public static class DashboardSnapshotBuilder
{
    public static DashboardSnapshot Build(DashboardInputs inputs)
    {
        var settings = inputs.Settings ?? new();
        var timeouts = settings.Timeouts ?? new();
        var now = Utc(inputs.NowUtc);
        var activeResets = (inputs.EarlyResets ?? inputs.EarlyResetStore?.GetActive(now) ?? [])
            .OfType<EarlyReset>().ToArray();
        var resetsByProvider = activeResets.ToLookup(r => r.Provider, StringComparer.OrdinalIgnoreCase);
        var samples = (inputs.HistorySamples ?? []).OfType<UsageHistorySample>().ToArray();
        var byProvider = samples.ToLookup(s => s.Provider, StringComparer.OrdinalIgnoreCase);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local));
        var providers = (inputs.Providers ?? []).OfType<UsageData>().Select(original =>
        {
            var data = original;
            // Attached status can outlive the service cache while the app remains open.
            if (data.ServiceStatus != null)
            {
                var statusAt = CacheRetention.StatusFetchedAt(data);
                data = CacheRetention.IsRetained(statusAt, now)
                    ? data with { LastSuccessfulStatusFetch = statusAt > now ? now : statusAt }
                    : data with { ServiceStatus = null, LastSuccessfulStatusFetch = null, StatusIsStale = false };
            }
            var id = data.Provider ?? string.Empty;
            var providerResets = resetsByProvider[id].ToArray();
            var resetsByWindow = providerResets.ToLookup(r => r.WindowKey, StringComparer.OrdinalIgnoreCase);
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
                var percent = double.IsFinite(window.Percent) ? Math.Max(0, window.Percent) : 0;
                var pace = double.IsFinite(window.Percent)
                    ? PaceCalculator.Project(percent, window.WindowMinutes, window.ResetAt, now)
                    : new PaceProjection(PaceKind.Unknown);
                var windowReset = resetsByWindow[historyKey].OrderByDescending(r => r.DetectedAtUtc).FirstOrDefault();
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
                        ? [] : history.Where(s => s.WindowKey == historyKey), window, now),
                    EarlyResetAtUtc = windowReset != null ? Utc(windowReset.DetectedAtUtc) : null,
                    EarlyResetFromPercent = windowReset?.PreviousPercent,
                    EarlyResetToPercent = windowReset?.CurrentPercent
                });
            }
            // Labels on UsageData require a non-null provider id.
            var labeled = data with { Provider = id };
            Add("session", DashboardWindowKind.Session, labeled.SessionLabel, data.Session);
            Add("weekly", DashboardWindowKind.Weekly, labeled.WeeklyLabel, data.Weekly);
            Add("tertiary", DashboardWindowKind.Tertiary, labeled.TertiaryLabel, data.Tertiary);
            foreach (var extra in extras)
                Add(UsageHistoryStore.ExtraWindowKey(extra), DashboardWindowKind.Extra, extra.Title, extra.Window);
            var cost = CacheRetention.RetainedCost(data.Cost, now) is { } raw
                ? raw with { Daily = (raw.Daily ?? []).OfType<DailyCost>().ToArray() } : null;
            if (cost != null && cost.IsStale && inputs.CostLedger != null)
            {
                var ledger30 = inputs.CostLedger.GetLast30Days(id, today);
                cost = cost.WithLedgerLast30Days(ledger30);
            }
            var oldReading = DataAge.OldReadingUtc(Utc(data.FetchedAt), data.DataUpdatedAt);
            var usageAt = Utc(data.LastSuccessfulFetch) ?? (data.HasUsage ? Utc(data.FetchedAt) : (DateTime?)null);
            var staleTimes = new List<DateTime>();
            if (data.UsageIsStale && usageAt.HasValue) staleTimes.Add(usageAt.Value);
            if (data.StatusIsStale && data.LastSuccessfulStatusFetch.HasValue) staleTimes.Add(Utc(data.LastSuccessfulStatusFetch.Value));
            if (cost?.IsStale == true) staleTimes.Add(Utc(cost.LastSuccessfulFetch ?? cost.FetchedAt));
            var lastSuccess = staleTimes.Count > 0 ? staleTimes.Min() : oldReading ?? usageAt;
            var status = data.ServiceStatus;
            var hasIncident = status != null && status.Indicator != ProviderStatusIndicator.None;
            var incidentTitle = hasIncident && !string.IsNullOrWhiteSpace(status!.IncidentTitle) ? status.IncidentTitle : null;
            var incidentUpdateText = hasIncident && !string.IsNullOrWhiteSpace(status!.LatestUpdateText) ? status.LatestUpdateText : null;
            var incidentUpdateAtUtc = hasIncident ? Utc(status!.LatestUpdateAt) : null;
            var mostRecentProviderReset = providerResets.OrderByDescending(r => r.DetectedAtUtc).FirstOrDefault();
            return new DashboardProvider
            {
                Id = id, DisplayName = id.ToLowerInvariant() switch
                { "claude" => "Claude", "codex" => "Codex", "gemini" => "Gemini", _ => id },
                Plan = data.Plan, Source = data.Status, ServiceStatus = data.ServiceStatus,
                IncidentTitle = incidentTitle, IncidentUpdateText = incidentUpdateText,
                IncidentUpdateAtUtc = incidentUpdateAtUtc,
                IsLoading = data.IsLoading,
                IsStale = data.UsageIsStale || data.StatusIsStale || cost?.IsStale == true || now - Utc(data.FetchedAt) > TimeSpan.FromMinutes(5) || oldReading != null,
                UsageIsStale = data.UsageIsStale, StatusIsStale = data.StatusIsStale,
                LastSuccessfulFetchUtc = lastSuccess, LastSuccessfulUsageFetchUtc = usageAt,
                LastSuccessfulStatusFetchUtc = Utc(data.LastSuccessfulStatusFetch),
                StaleAge = lastSuccess.HasValue ? Age(now, lastSuccess.Value) : null,
                Error = data.Error, FetchedAtUtc = Utc(data.FetchedAt), OldReadingUtc = oldReading, Credits = data.Credits,
                Windows = windows, Cost = settings.ShowCostTotals && cost?.HasData == true ? BuildCost(cost, today, now) : null,
                HistorySampleCount = history.Length, HistoryOldestUtc = Oldest(history),
                EarlyResetAtUtc = mostRecentProviderReset != null ? Utc(mostRecentProviderReset.DetectedAtUtc) : null,
                EarlyResetFromPercent = mostRecentProviderReset?.PreviousPercent,
                EarlyResetToPercent = mostRecentProviderReset?.CurrentPercent
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
                WindowCount = refs.Length, WindowsAtLimit = refs.Count(w => w.Window.UsedPercent >= UsageThresholds.Critical),
                WindowsNearLimit = refs.Count(w => w.Window.UsedPercent is >= UsageThresholds.Warning and < UsageThresholds.Critical),
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

    private static DashboardCost BuildCost(CostData cost, DateOnly today, DateTime now)
    {
        var daily = CostSeriesBuilder.Daily(cost, today, 30);
        var active = daily.Where(d => d.TotalTokens > 0 || d.TotalCost > 0 || d.InputTokens > 0 ||
            d.OutputTokens > 0 || d.CacheReadTokens > 0 || d.CacheCreationTokens > 0 || d.ReasoningTokens > 0 ||
            d.ModelBreakdowns.Any(m => m.TotalTokens > 0 || m.Cost > 0)).ToArray();
        return new DashboardCost
        {
            IsStale = cost.IsStale, LastSuccessfulFetchUtc = Utc(cost.LastSuccessfulFetch ?? cost.FetchedAt),
            StaleAge = Age(now, Utc(cost.LastSuccessfulFetch ?? cost.FetchedAt)),
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
    private static TimeSpan Age(DateTime now, DateTime fetchedAt) => now > fetchedAt ? now - fetchedAt : TimeSpan.Zero;
    private static DateTime? Utc(DateTime? value) => value.HasValue ? DashboardValues.Utc(value.Value) : null;
    private static DateTime Utc(DateTime value) => DashboardValues.Utc(value);
}
