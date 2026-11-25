using System.Text.Json;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.Services.Native.Cost;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services.Native;

/// <summary>Scans the local Claude and Codex logs natively and returns costs as the JSON the CodexBar CLI prints.</summary>
public interface INativeCostService
{
    /// <summary>
    /// Brings the cost index up to date once, then returns one report per requested provider in the shape of
    /// <c>codexbar cost --format json</c>. Gemini has no local logs and gets the CLI's empty reading.
    /// </summary>
    /// <exception cref="ArgumentException">A provider id is not an allowed provider.</exception>
    Task<IReadOnlyDictionary<string, string?>> FetchCostJsonAsync(IReadOnlyList<string> providers,
        DateTimeOffset asOf, CancellationToken ct = default);
}

/// <summary>
/// Owns the incremental scanner (index in the app's data folder), the change notifications on the Windows log
/// folders and the price store. All work runs off the UI thread at background priority.
/// </summary>
public sealed class NativeCostService : INativeCostService, IDisposable
{
    internal const string IndexFolderName = "cost-index";
    internal const int HistoryDays = 30;

    private readonly IReadOnlyList<CostLogRoot> _roots;
    private readonly NativeCostScanner _scanner;
    private readonly CostLogWatcher _watcher;
    private readonly CostPriceStore _prices;
    private readonly ILogger<NativeCostService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _refreshPrices;

    /// <summary>Production constructor: the signed-in user's log folders and the folder of settings.json.</summary>
    public NativeCostService(ISettingsService settings, ILoggerFactory loggerFactory)
        : this(Path.GetDirectoryName(Path.GetFullPath(settings.SettingsFilePath))!,
            DefaultRoots(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), loggerFactory)
    {
    }

    /// <summary>Test constructor: explicit folders, optionally without OS watchers or price downloads.</summary>
    public NativeCostService(string dataDirectory, IReadOnlyList<CostLogRoot> roots, ILoggerFactory loggerFactory,
        TimeProvider? clock = null, TimeZoneInfo? zone = null, bool watch = true, bool refreshPrices = true,
        CostPriceStore? prices = null)
    {
        _roots = roots;
        _logger = loggerFactory.CreateLogger<NativeCostService>();
        _scanner = new NativeCostScanner(Path.Combine(dataDirectory, IndexFolderName),
            loggerFactory.CreateLogger<NativeCostScanner>(), zone);
        _watcher = new CostLogWatcher(roots, loggerFactory.CreateLogger<CostLogWatcher>(), clock, watch);
        _prices = prices ?? new CostPriceStore(dataDirectory, loggerFactory.CreateLogger<CostPriceStore>(), clock);
        _refreshPrices = refreshPrices;
    }

    /// <summary>
    /// The Windows log folders: Claude Code's projects, and Codex's sessions plus archived sessions (upstream scans
    /// both). Logs inside WSL are out of scope.
    /// </summary>
    public static IReadOnlyList<CostLogRoot> DefaultRoots(string userProfile) =>
    [
        new(CostProvider.Claude, Path.Combine(userProfile, ".claude", "projects")),
        new(CostProvider.Codex, Path.Combine(userProfile, ".codex", "sessions")),
        new(CostProvider.Codex, Path.Combine(userProfile, ".codex", "archived_sessions"))
    ];

    public async Task<IReadOnlyDictionary<string, string?>> FetchCostJsonAsync(IReadOnlyList<string> providers,
        DateTimeOffset asOf, CancellationToken ct = default)
    {
        var normalized = providers.Select(ProviderConstants.ValidateAndNormalize).Distinct(StringComparer.Ordinal).ToArray();
        ct.ThrowIfCancellationRequested();
        // At most one download a day, off the caller's thread; new prices apply from the next report.
        if (_refreshPrices)
            Task.Run(() => _prices.RefreshIfDueAsync(CancellationToken.None)).SafeFireAndForget(
                ex => _logger.LogDebug("Price refresh failed ({Type})", ex.GetType().Name));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Scan(normalized, asOf, ct), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private IReadOnlyDictionary<string, string?> Scan(string[] providers, DateTimeOffset asOf, CancellationToken ct)
    {
        var plan = _watcher.TakePlan();
        try
        {
            // Two readers only when the folders are listed in full (the first scan reads every log once).
            var result = _scanner.Update(_roots, plan.FullListing ? 2 : 1, ct, plan.FullListing ? null : plan.ChangedPaths);
            _watcher.Complete(plan, succeeded: true);
            _logger.LogDebug("Native cost update ({Mode}): checked {Checked} files, read {Files} files and {Bytes} bytes",
                plan.FullListing ? "full listing" : "changed files", result.DiscoveredFiles, result.FilesRead, result.BytesRead);
        }
        catch
        {
            _watcher.Complete(plan, succeeded: false);
            throw;
        }

        var pricing = _prices.GetPricing();
        var reports = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            CostProvider? source = provider switch
            {
                "claude" => CostProvider.Claude,
                "codex" => CostProvider.Codex,
                _ => null
            };
            // The CLI reports Gemini (its "antigravity" provider) as a successful reading without any usage; there
            // are no local Gemini logs to scan, so the native report says the same.
            var report = source is { } known
                ? _scanner.ReadReport(known, pricing, asOf, HistoryDays, ct)
                : provider == "gemini" ? EmptyReport("antigravity") : null;
            reports[provider] = report is null ? null
                : JsonSerializer.Serialize(report, AppJsonSerializerContext.Default.CostDataDto);
        }
        return reports;
    }

    private static CostDataDto EmptyReport(string cliProvider) => new()
    {
        Provider = cliProvider, CurrencyCode = "USD", HistoryDays = HistoryDays, Source = "local", Provenance = "unknown",
        Coverage = new(), SessionCostUSD = 0, SessionTokens = 0, Last30DaysCostUSD = 0, Last30DaysTokens = 0, Daily = []
    };

    public void Dispose() => _watcher.Dispose();
}
