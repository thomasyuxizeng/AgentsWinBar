using System.Text.Json;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;
using AgentsWinBar.Services.Native.Cost;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services.Native;

/// <summary>
/// Scans the local Claude and Codex logs and Antigravity's conversation databases natively and returns costs as the
/// JSON the CodexBar CLI prints.
/// </summary>
public interface INativeCostService
{
    /// <summary>
    /// Brings the cost index up to date once, then returns one report per requested provider in the shape of
    /// <c>codexbar cost --format json</c>. A provider without any local history (Gemini before agy wrote its first
    /// conversation) maps to null: it has no cost to show, which is not a failure.
    /// </summary>
    /// <exception cref="ArgumentException">A provider id is not an allowed provider.</exception>
    Task<IReadOnlyDictionary<string, string?>> FetchCostJsonAsync(IReadOnlyList<string> providers,
        DateTimeOffset asOf, CancellationToken ct = default);
}

/// <summary>Direct typed DTO route avoiding JSON serialize-then-deserialize roundtrips in-process.</summary>
public interface INativeTypedCostService : INativeCostService
{
    Task<IReadOnlyDictionary<string, CostDataDto?>> FetchCostDtosAsync(IReadOnlyList<string> providers,
        DateTimeOffset asOf, CancellationToken ct = default);
}

/// <summary>
/// Owns the incremental scanner (index in the app's data folder), the change notifications on the Windows log
/// folders and the price store. All work runs off the UI thread at background priority.
/// </summary>
public sealed class NativeCostService : INativeTypedCostService, IDisposable
{
    internal const string IndexFolderName = "cost-index";
    internal const int HistoryDays = 30;
    /// <summary>How many updates in a row a log that could not be opened is tried again.</summary>
    internal const int MaxSkipRetries = 3;

    private readonly IReadOnlyList<CostLogRoot> _roots;
    private readonly NativeCostScanner _scanner;
    private readonly CostLogWatcher _watcher;
    private readonly CostPriceStore _prices;
    private readonly ILogger<NativeCostService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _refreshPrices;
    /// <summary>Unreadable logs and how many updates in a row they failed (only touched under the gate).</summary>
    private Dictionary<string, int> _skipRetries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Production constructor: the signed-in user's log folders (under the fake profile of
    /// <paramref name="testOverrides"/>, which is always null in a Release build) and the folder of settings.json.
    /// </summary>
    public NativeCostService(ISettingsService settings, ILoggerFactory loggerFactory, NativeTestOverrides? testOverrides = null)
        : this(Path.GetDirectoryName(Path.GetFullPath(settings.SettingsFilePath))!,
            DefaultRoots(testOverrides?.ProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                testOverrides is null ? Environment.GetEnvironmentVariable(GeminiHomeVariable) : null), loggerFactory)
    {
    }

    /// <summary>Test constructor: explicit folders, optionally without OS watchers or price downloads.</summary>
    /// <param name="scanner">A scanner to use instead of the default one in <paramref name="dataDirectory"/> (tests inject failures).</param>
    public NativeCostService(string dataDirectory, IReadOnlyList<CostLogRoot> roots, ILoggerFactory loggerFactory,
        TimeProvider? clock = null, TimeZoneInfo? zone = null, bool watch = true, bool refreshPrices = true,
        CostPriceStore? prices = null, NativeCostScanner? scanner = null)
    {
        _roots = roots;
        _logger = loggerFactory.CreateLogger<NativeCostService>();
        _scanner = scanner ?? new NativeCostScanner(Path.Combine(dataDirectory, IndexFolderName),
            loggerFactory.CreateLogger<NativeCostScanner>(), zone);
        _watcher = new CostLogWatcher(roots, loggerFactory.CreateLogger<CostLogWatcher>(), clock, watch);
        _prices = prices ?? new CostPriceStore(dataDirectory, loggerFactory.CreateLogger<CostPriceStore>(), clock);
        _refreshPrices = refreshPrices;
    }

    /// <summary>Replaces <c>%USERPROFILE%\.gemini</c> as the Gemini home, as in upstream (and the Gemini CLI).</summary>
    internal const string GeminiHomeVariable = "GEMINI_CLI_HOME";

    /// <summary>
    /// The Windows log folders: Claude Code's projects, Codex's sessions plus archived sessions (upstream scans both),
    /// and Antigravity's conversation databases (<see cref="GeminiRoots"/>). Logs inside WSL are out of scope.
    /// </summary>
    /// <param name="geminiHome">An absolute Gemini home that replaces <c>.gemini</c> in the profile; anything else is ignored.</param>
    public static IReadOnlyList<CostLogRoot> DefaultRoots(string userProfile, string? geminiHome = null) =>
    [
        new(CostProvider.Claude, Path.Combine(userProfile, ".claude", "projects")),
        new(CostProvider.Codex, Path.Combine(userProfile, ".codex", "sessions")),
        new(CostProvider.Codex, Path.Combine(userProfile, ".codex", "archived_sessions")),
        .. GeminiRoots(geminiHome?.Trim() is { Length: > 0 } home && Path.IsPathFullyQualified(home) && !home.Contains('\0')
            ? home : Path.Combine(userProfile, ".gemini"))
    ];

    /// <summary>
    /// Upstream's three recognized Antigravity roots in a Gemini home (AntigravityLocalScan.swift at a26ca57): the CLI's
    /// conversations, and the app data folder and its conversations. Each is read without its subfolders.
    /// </summary>
    public static IReadOnlyList<CostLogRoot> GeminiRoots(string geminiHome) =>
    [
        new(CostProvider.Gemini, Path.Combine(geminiHome, "antigravity-cli", "conversations")),
        new(CostProvider.Gemini, Path.Combine(geminiHome, "antigravity")),
        new(CostProvider.Gemini, Path.Combine(geminiHome, "antigravity", "conversations"))
    ];

    public async Task<IReadOnlyDictionary<string, string?>> FetchCostJsonAsync(IReadOnlyList<string> providers,
        DateTimeOffset asOf, CancellationToken ct = default)
    {
        var dtos = await FetchCostDtosAsync(providers, asOf, ct).ConfigureAwait(false);
        var reports = new Dictionary<string, string?>(dtos.Count, StringComparer.Ordinal);
        foreach (var (provider, report) in dtos)
        {
            reports[provider] = report is null ? null
                : JsonSerializer.Serialize(report, AppJsonSerializerContext.Default.CostDataDto);
        }
        return reports;
    }

    public async Task<IReadOnlyDictionary<string, CostDataDto?>> FetchCostDtosAsync(IReadOnlyList<string> providers,
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

    private IReadOnlyDictionary<string, CostDataDto?> Scan(string[] providers, DateTimeOffset asOf, CancellationToken ct)
    {
        var plan = _watcher.TakePlan();
        // A deleted index is rebuilt by a listing now; the changed paths alone would leave it empty.
        if (!plan.FullListing && !_scanner.IndexExists) plan = new(true, []);
        var pricing = _prices.GetPricing();
        try
        {
            return UpdateAndBuildReports(plan, providers, pricing, asOf, ct);
        }
        catch (Exception ex) when (IsDamagedIndex(ex))
        {
            // A damaged index is replaced now (opening it for an update validates it), not at the next listing.
            _logger.LogInformation("Native cost index is damaged; rebuilding it");
            _watcher.NotifyLost();
            return UpdateAndBuildReports(_watcher.TakePlan(), providers, pricing, asOf, ct);
        }
    }

    /// <summary>
    /// True for a damaged-index error however it arrives: thrown directly, or wrapped in an
    /// <see cref="AggregateException"/> by a parallel reader.
    /// </summary>
    internal static bool IsDamagedIndex(Exception exception) => exception switch
    {
        CostIndexDatabase.SqliteException sqlite => sqlite.InvalidIndex,
        AggregateException aggregate => aggregate.Flatten().InnerExceptions.Any(IsDamagedIndex),
        _ => false
    };

    private IReadOnlyDictionary<string, CostDataDto?> UpdateAndBuildReports(
        CostScanPlan plan,
        string[] providers,
        CostPricing pricing,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        var reportProviders = new List<CostProvider>(3);
        foreach (var provider in providers)
        {
            var source = Source(provider);
            if (source is { } known && !reportProviders.Contains(known))
            {
                reportProviders.Add(known);
            }
        }

        CostScanResult result;
        IReadOnlyDictionary<CostProvider, CostDataDto> scanned;
        try
        {
            // Two readers only when the folders are listed in full (the first scan reads every log once).
            (result, scanned) = _scanner.UpdateAndReadReports(
                _roots,
                plan.FullListing ? 2 : 1,
                reportProviders,
                pricing,
                asOf,
                HistoryDays,
                ct,
                plan.FullListing ? null : plan.ChangedPaths);
            // A rebuilt index was filled by a full listing even when only changed files were asked for.
            var listed = plan.FullListing || result.IndexRebuilt;
            _watcher.Complete(listed ? new CostScanPlan(true, []) : plan, succeeded: true);
            _logger.LogDebug("Native cost update ({Mode}): checked {Checked} files, read {Files} files and {Bytes} bytes",
                listed ? "full listing" : "changed files", result.DiscoveredFiles, result.FilesRead, result.BytesRead);
        }
        catch
        {
            _watcher.Complete(plan, succeeded: false);
            throw;
        }

        // A log that could not be opened (locked by its writer, for example) is tried again on the next few
        // updates instead of waiting for the periodic listing; one that stays unreadable is left to that listing.
        var retries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in result.SkippedPaths ?? [])
        {
            var attempt = _skipRetries.GetValueOrDefault(path) + 1;
            if (attempt > MaxSkipRetries) continue;
            retries[path] = attempt;
            _watcher.Retry(path);
        }
        _skipRetries = retries;
        // A database read while agy had it open is looked at again next time, in case its log's notification is late.
        foreach (var path in result.RecheckPaths ?? []) _watcher.Retry(path);
        if (result.Gemini is { } gemini)
            _logger.LogDebug("Antigravity databases: read {Databases}, {Calls} calls, {Undated} without a time",
                gemini.Databases, gemini.Calls, gemini.Undated);

        var reports = new Dictionary<string, CostDataDto?>(providers.Length, StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            if (Source(provider) is not { } known) reports[provider] = null;
            else if (scanned.TryGetValue(known, out var report)) reports[provider] = report;
            // No history yet. Databases that exist but could not be read yet are a failed reading (the last good one
            // stays); with none at all there is simply nothing to show.
            else reports[provider] = retries.Keys.Any(path => NativeCostScanner.IsLog(path, known))
                ? Unreadable(known)
                : null;
        }
        return reports;
    }

    private static CostProvider? Source(string provider) => provider switch
    {
        "claude" => CostProvider.Claude,
        "codex" => CostProvider.Codex,
        "gemini" => CostProvider.Gemini,
        _ => null
    };

    private static CostDataDto Unreadable(CostProvider provider) => new()
    {
        Provider = provider == CostProvider.Gemini ? "antigravity" : provider.ToString().ToLowerInvariant(),
        Source = "local", HistoryDays = HistoryDays, Error = "The local history could not be read yet."
    };

    /// <summary>The change notifications (tests feed them directly when OS watching is off).</summary>
    internal CostLogWatcher Watcher => _watcher;

    public void Dispose() => _watcher.Dispose();
}
