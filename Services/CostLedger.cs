using System.Text.Json;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Permanent cost ledger persisting to cost-ledger.json beside the settings file.
/// Retains lifetime daily cost and token series across local log rotations.
/// </summary>
public sealed class CostLedger : ICostLedger
{
    private readonly ISettingsService _settings;
    private readonly ILogger<CostLedger> _logger;
    private readonly TimeProvider _clock;
    private readonly string _costCachePath;
    private readonly string _bakPath;
    private readonly string _tmpPath;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly object _stateLock = new();

    private CostLedgerFile _file = new();
    private volatile bool _isLoaded;
    private int _loadQueued;

    public string FilePath { get; }

    public CostLedger(
        ISettingsService settings,
        ILogger<CostLedger> logger,
        TimeProvider? timeProvider = null,
        string? filePath = null,
        string? costCachePath = null)
    {
        _settings = settings;
        _logger = logger;
        _clock = timeProvider ?? TimeProvider.System;

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(settings.SettingsFilePath))!;
        FilePath = filePath ?? Path.Combine(baseDir, "cost-ledger.json");
        _costCachePath = costCachePath ?? Path.Combine(baseDir, "cost-cache.json");
        _bakPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(FilePath))!, "cost-ledger.bak");
        _tmpPath = FilePath + ".tmp";
    }

    private bool DeveloperMode => _settings.Settings.DeveloperModeEnabled;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_isLoaded || DeveloperMode) return;
        var acquired = false;
        try
        {
            await _ioGate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            if (DeveloperMode) return;
            await LoadCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cost ledger load encountered an unexpected error");
        }
        finally
        {
            if (acquired) _ioGate.Release();
        }
    }

    private bool EnsureLoadStarted()
    {
        if (_isLoaded) return true;
        if (!DeveloperMode && Interlocked.CompareExchange(ref _loadQueued, 1, 0) == 0)
        {
            // Queries must never perform file IO or wait for the IO gate on their caller's thread.
            Task.Run(async () =>
            {
                try
                {
                    await LoadAsync().ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref _loadQueued, 0);
                }
            }).SafeFireAndForget(onError: ex => _logger.LogWarning(ex, "Background cost ledger load failed"));
        }
        return false;
    }

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        if (_isLoaded) return;
        var nowUtc = _clock.GetUtcNow().UtcDateTime;

        if (!File.Exists(FilePath))
        {
            // First run: backfill from existing cost-cache.json if present
            var backfilled = await TryBackfillFromCostCacheAsync(ct).ConfigureAwait(false);
            if (backfilled)
            {
                await SaveCoreAsync(nowUtc, ct).ConfigureAwait(false);
            }
            _isLoaded = true;
            return;
        }

        string rawJson;
        try
        {
            rawJson = await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read cost ledger file at {Path}", FilePath);
            RenameToCorrupt(FilePath, nowUtc);
            await TryRecoverFromBackupAsync(nowUtc, ct).ConfigureAwait(false);
            _isLoaded = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            _logger.LogWarning("Cost ledger file at {Path} is empty; renaming to corrupt", FilePath);
            RenameToCorrupt(FilePath, nowUtc);
            await TryRecoverFromBackupAsync(nowUtc, ct).ConfigureAwait(false);
            _isLoaded = true;
            return;
        }

        CostLedgerFile? deserialized = null;
        try
        {
            deserialized = JsonSerializer.Deserialize(rawJson, AppJsonSerializerContext.Default.CostLedgerFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse cost ledger JSON at {Path}; renaming to corrupt", FilePath);
        }

        if (deserialized == null || deserialized.SchemaVersion != 1)
        {
            _logger.LogWarning("Cost ledger at {Path} has invalid or unsupported schema version {Version}; renaming to corrupt",
                FilePath, deserialized?.SchemaVersion);
            RenameToCorrupt(FilePath, nowUtc);
            await TryRecoverFromBackupAsync(nowUtc, ct).ConfigureAwait(false);
            _isLoaded = true;
            return;
        }

        lock (_stateLock)
        {
            _file = deserialized;
        }
        _isLoaded = true;
    }

    private async Task<bool> TryRecoverFromBackupAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (File.Exists(_bakPath))
        {
            try
            {
                var bakJson = await File.ReadAllTextAsync(_bakPath, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(bakJson))
                {
                    var recovered = JsonSerializer.Deserialize(bakJson, AppJsonSerializerContext.Default.CostLedgerFile);
                    if (recovered != null && recovered.SchemaVersion == 1)
                    {
                        lock (_stateLock) { _file = recovered; }
                        await SaveCoreAsync(nowUtc, ct).ConfigureAwait(false);
                        _logger.LogInformation("Successfully recovered cost ledger from backup {BakPath}", _bakPath);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to recover from cost ledger backup {BakPath}", _bakPath);
            }
        }

        // Start fresh ledger
        lock (_stateLock)
        {
            _file = new CostLedgerFile();
        }
        await TryBackfillFromCostCacheAsync(ct).ConfigureAwait(false);
        return false;
    }

    private async Task<bool> TryBackfillFromCostCacheAsync(CancellationToken ct)
    {
        if (!File.Exists(_costCachePath)) return false;
        try
        {
            var json = await File.ReadAllTextAsync(_costCachePath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return false;
            var cacheFile = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.CostCacheFile);
            if (cacheFile?.Items == null || cacheFile.Items.Count == 0) return false;

            var changed = false;
            foreach (var (key, costData) in cacheFile.Items)
            {
                if (costData == null || !costData.HasData || !ProviderConstants.IsValidProvider(key)) continue;
                MergeScanUnlocked(costData, DashboardValues.Utc(costData.LastSuccessfulFetch ?? costData.FetchedAt));
                changed = true;
            }
            return changed;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Backfill from cost-cache.json skipped due to read error");
            return false;
        }
    }

    private void RenameToCorrupt(string path, DateTime nowUtc)
    {
        try
        {
            if (!File.Exists(path)) return;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var baseName = $"cost-ledger.corrupt-{nowUtc:yyyyMMddTHHmmssZ}";
            var corruptPath = Path.Combine(dir, $"{baseName}.json");
            var suffix = 1;
            while (File.Exists(corruptPath))
            {
                corruptPath = Path.Combine(dir, $"{baseName}-{suffix++}.json");
            }
            File.Move(path, corruptPath, overwrite: false);
            _logger.LogWarning("Renamed corrupt cost ledger from {Source} to {Target}", path, corruptPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rename corrupt cost ledger file {Path}", path);
        }
    }

    public Task RecordScanAsync(CostData scan, DateTime? nowUtc = null, CancellationToken ct = default) =>
        scan == null ? Task.CompletedTask : RecordScansAsync([scan], nowUtc, ct);

    public async Task RecordScansAsync(IReadOnlyList<CostData> scans, DateTime? nowUtc = null, CancellationToken ct = default)
    {
        if (DeveloperMode) return;
        if (scans == null || scans.Count == 0) return;

        var validScans = scans.Where(scan =>
            scan != null &&
            scan.HasData &&
            scan.FetchOutcome == FetchOutcome.Success &&
            !string.IsNullOrWhiteSpace(scan.Provider) &&
            ProviderConstants.IsValidProvider(scan.Provider)).ToList();

        if (validScans.Count == 0) return;

        // Leave the caller thread before any file IO (the scan callback may run on the UI thread).
        await Task.Yield();

        var acquired = false;
        try
        {
            await _ioGate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            await LoadCoreAsync(ct).ConfigureAwait(false);

            var now = nowUtc ?? _clock.GetUtcNow().UtcDateTime;

            CostLedgerFile backup;
            lock (_stateLock)
            {
                backup = CloneFile(_file);
            }

            foreach (var scan in validScans)
            {
                MergeScanUnlocked(scan, now);
            }

            bool saved;
            try
            {
                saved = await SaveCoreAsync(now, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (_stateLock)
                {
                    _file = backup;
                }
                throw;
            }

            if (!saved)
            {
                lock (_stateLock)
                {
                    _file = backup;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record cost scans for {Count} providers", validScans.Count);
        }
        finally
        {
            if (acquired) _ioGate.Release();
        }
    }

    private static CostLedgerFile CloneFile(CostLedgerFile file)
    {
        var clone = new CostLedgerFile { SchemaVersion = file.SchemaVersion };
        if (file.Providers != null)
        {
            foreach (var (provider, days) in file.Providers)
            {
                clone.Providers[provider] = days != null
                    ? new Dictionary<string, CostLedgerDayEntry>(days, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, CostLedgerDayEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }
        return clone;
    }

    private void MergeScanUnlocked(CostData scan, DateTime nowUtc)
    {
        var provider = ProviderConstants.ValidateAndNormalize(scan.Provider);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DashboardValues.Utc(nowUtc), _clock.LocalTimeZone));

        lock (_stateLock)
        {
            if (!_file.Providers.TryGetValue(provider, out var providerDays))
            {
                providerDays = new Dictionary<string, CostLedgerDayEntry>(StringComparer.OrdinalIgnoreCase);
                _file.Providers[provider] = providerDays;
            }

            // Group scan daily entries by date
            var scanDays = (scan.Daily ?? []).OfType<DailyCost>().GroupBy(d => d.Date).ToDictionary(g => g.Key, g =>
            {
                var list = g.ToList();
                return new DailyCost
                {
                    Date = g.Key,
                    TotalCost = list.Sum(d => Math.Max(0, d.TotalCost)),
                    TotalTokens = list.Sum(d => Math.Max(0, d.TotalTokens)),
                    InputTokens = list.Sum(d => Math.Max(0, d.InputTokens)),
                    OutputTokens = list.Sum(d => Math.Max(0, d.OutputTokens)),
                    CacheReadTokens = list.Sum(d => Math.Max(0, d.CacheReadTokens)),
                    CacheCreationTokens = list.Sum(d => Math.Max(0, d.CacheCreationTokens)),
                    ReasoningTokens = list.Sum(d => Math.Max(0, d.ReasoningTokens)),
                    ModelBreakdowns = list.SelectMany(d => d.ModelBreakdowns ?? []).OfType<ModelCost>().ToArray()
                };
            });

            // If scan.Daily had no entries at all, but scan reports today totals, include it
            if (scanDays.Count == 0 && (scan.TodayCostUSD > 0 || scan.TodayTokens > 0))
            {
                scanDays[today] = new DailyCost
                {
                    Date = today,
                    TotalCost = scan.TodayCostUSD,
                    TotalTokens = scan.TodayTokens
                };
            }

            foreach (var (date, scanDay) in scanDays)
            {
                var dateKey = date.ToString("yyyy-MM-dd");
                providerDays.TryGetValue(dateKey, out var existing);

                var existingHasData = existing != null && existing.HasData;
                var scanHasData = scanDay.TotalCost > 0 || scanDay.TotalTokens > 0 ||
                    scanDay.InputTokens > 0 || scanDay.OutputTokens > 0 ||
                    scanDay.CacheReadTokens > 0 || scanDay.CacheCreationTokens > 0 ||
                    scanDay.ReasoningTokens > 0 ||
                    (scanDay.ModelBreakdowns ?? []).Any(m => (m.Cost ?? 0) > 0 || m.TotalTokens > 0);

                // Zero-day guard: never replace a day that has data in the ledger with an all-zero/empty day from the scan
                if (existingHasData && !scanHasData)
                {
                    continue;
                }

                if (date == today)
                {
                    // Today's partial day just updates upward over the day
                    if (existing == null)
                    {
                        providerDays[dateKey] = new CostLedgerDayEntry
                        {
                            Date = date,
                            CostUSD = scanDay.TotalCost,
                            TotalTokens = scanDay.TotalTokens,
                            InputTokens = scanDay.InputTokens,
                            OutputTokens = scanDay.OutputTokens,
                            CacheReadTokens = scanDay.CacheReadTokens,
                            CacheWriteTokens = scanDay.CacheCreationTokens,
                            ReasoningTokens = scanDay.ReasoningTokens,
                            Models = (scanDay.ModelBreakdowns ?? []).Select(m => new CostLedgerModelEntry
                            {
                                ModelName = m.ModelName,
                                CostUSD = m.Cost,
                                TotalTokens = m.TotalTokens
                            }).ToArray(),
                            LastUpdatedUtc = nowUtc
                        };
                    }
                    else
                    {
                        var mergedModels = MergeModelsUpward(existing.Models, scanDay.ModelBreakdowns);
                        providerDays[dateKey] = existing with
                        {
                            CostUSD = Math.Max(existing.CostUSD, scanDay.TotalCost),
                            TotalTokens = Math.Max(existing.TotalTokens, scanDay.TotalTokens),
                            InputTokens = Math.Max(existing.InputTokens, scanDay.InputTokens),
                            OutputTokens = Math.Max(existing.OutputTokens, scanDay.OutputTokens),
                            CacheReadTokens = Math.Max(existing.CacheReadTokens, scanDay.CacheReadTokens),
                            CacheWriteTokens = Math.Max(existing.CacheWriteTokens, scanDay.CacheCreationTokens),
                            ReasoningTokens = Math.Max(existing.ReasoningTokens, scanDay.ReasoningTokens),
                            Models = mergedModels,
                            LastUpdatedUtc = nowUtc
                        };
                    }
                }
                else
                {
                    // Past day: scan is authoritative for days it covers
                    providerDays[dateKey] = new CostLedgerDayEntry
                    {
                        Date = date,
                        CostUSD = scanDay.TotalCost,
                        TotalTokens = scanDay.TotalTokens,
                        InputTokens = scanDay.InputTokens,
                        OutputTokens = scanDay.OutputTokens,
                        CacheReadTokens = scanDay.CacheReadTokens,
                        CacheWriteTokens = scanDay.CacheCreationTokens,
                        ReasoningTokens = scanDay.ReasoningTokens,
                        Models = (scanDay.ModelBreakdowns ?? []).Select(m => new CostLedgerModelEntry
                        {
                            ModelName = m.ModelName,
                            CostUSD = m.Cost,
                            TotalTokens = m.TotalTokens
                        }).ToArray(),
                        LastUpdatedUtc = nowUtc
                    };
                }
            }
        }
    }

    private static IReadOnlyList<CostLedgerModelEntry> MergeModelsUpward(
        IReadOnlyList<CostLedgerModelEntry> existingModels,
        IReadOnlyList<ModelCost>? scanModels)
    {
        var map = (existingModels ?? []).ToDictionary(m => m.ModelName, m => m, StringComparer.OrdinalIgnoreCase);
        foreach (var sm in scanModels ?? [])
        {
            if (string.IsNullOrWhiteSpace(sm.ModelName)) continue;
            if (map.TryGetValue(sm.ModelName, out var em))
            {
                decimal? cost = null;
                if (em.CostUSD.HasValue || sm.Cost.HasValue)
                {
                    cost = Math.Max(em.CostUSD ?? 0, sm.Cost ?? 0);
                }
                map[sm.ModelName] = em with
                {
                    TotalTokens = Math.Max(em.TotalTokens, sm.TotalTokens),
                    CostUSD = cost
                };
            }
            else
            {
                map[sm.ModelName] = new CostLedgerModelEntry
                {
                    ModelName = sm.ModelName,
                    CostUSD = sm.Cost,
                    TotalTokens = sm.TotalTokens
                };
            }
        }
        return map.Values.OrderByDescending(m => m.CostUSD ?? 0).ThenBy(m => m.ModelName, StringComparer.Ordinal).ToArray();
    }

    private async Task<bool> SaveCoreAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (DeveloperMode) return false;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
            Directory.CreateDirectory(dir);

            // Rolling backup of the last good file
            if (File.Exists(FilePath))
            {
                try
                {
                    File.Copy(FilePath, _bakPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Rolling backup copy to {BakPath} skipped", _bakPath);
                }
            }

            CostLedgerFile snapshot;
            lock (_stateLock)
            {
                snapshot = _file;
            }

            var json = JsonSerializer.Serialize(snapshot, AppJsonSerializerContext.Default.CostLedgerFile);

            await File.WriteAllTextAsync(_tmpPath, json, ct).ConfigureAwait(false);
            File.Move(_tmpPath, FilePath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Atomic write failure (e.g. read-only directory, permission error) must never throw
            _logger.LogWarning(ex, "Cost ledger could not be saved to {Path}", FilePath);
            return false;
        }
    }

    public CostLedgerLifetimeResult GetLifetime(string? provider = null)
    {
        if (!EnsureLoadStarted()) return new CostLedgerLifetimeResult();
        lock (_stateLock)
        {
            var providersToInclude = provider != null && ProviderConstants.IsValidProvider(provider)
                ? [_file.Providers.FirstOrDefault(p => string.Equals(p.Key, ProviderConstants.ValidateAndNormalize(provider), StringComparison.OrdinalIgnoreCase))]
                : _file.Providers.ToArray();

            var byProvider = new Dictionary<string, CostLedgerProviderTotal>(StringComparer.OrdinalIgnoreCase);

            decimal grandTotalCost = 0;
            long grandTotalTokens = 0;
            long grandInputTokens = 0;
            long grandOutputTokens = 0;
            long grandCacheRead = 0;
            long grandCacheWrite = 0;
            long grandReasoning = 0;

            var allDatesWithData = new HashSet<DateOnly>();

            foreach (var kvp in providersToInclude)
            {
                if (kvp.Key == null || kvp.Value == null) continue;
                var pName = kvp.Key;
                var days = kvp.Value.Values.Where(d => d.HasData).OrderBy(d => d.Date).ToList();

                var pTotalCost = days.Sum(d => d.CostUSD);
                var pTotalTokens = days.Sum(d => d.TotalTokens);
                var pInputTokens = days.Sum(d => d.InputTokens);
                var pOutputTokens = days.Sum(d => d.OutputTokens);
                var pCacheRead = days.Sum(d => d.CacheReadTokens);
                var pCacheWrite = days.Sum(d => d.CacheWriteTokens);
                var pReasoning = days.Sum(d => d.ReasoningTokens);

                foreach (var d in days) allDatesWithData.Add(d.Date);

                byProvider[pName] = new CostLedgerProviderTotal
                {
                    Provider = pName,
                    TotalCostUSD = pTotalCost,
                    TotalTokens = pTotalTokens,
                    InputTokens = pInputTokens,
                    OutputTokens = pOutputTokens,
                    CacheReadTokens = pCacheRead,
                    CacheWriteTokens = pCacheWrite,
                    ReasoningTokens = pReasoning,
                    FirstDayWithData = days.Count > 0 ? days[0].Date : null,
                    LastDayWithData = days.Count > 0 ? days[^1].Date : null,
                    DaysWithDataCount = days.Count
                };

                grandTotalCost += pTotalCost;
                grandTotalTokens += pTotalTokens;
                grandInputTokens += pInputTokens;
                grandOutputTokens += pOutputTokens;
                grandCacheRead += pCacheRead;
                grandCacheWrite += pCacheWrite;
                grandReasoning += pReasoning;
            }

            var sortedDates = allDatesWithData.OrderBy(d => d).ToList();

            return new CostLedgerLifetimeResult
            {
                TotalCostUSD = grandTotalCost,
                TotalTokens = grandTotalTokens,
                InputTokens = grandInputTokens,
                OutputTokens = grandOutputTokens,
                CacheReadTokens = grandCacheRead,
                CacheWriteTokens = grandCacheWrite,
                ReasoningTokens = grandReasoning,
                FirstDayWithData = sortedDates.Count > 0 ? sortedDates[0] : null,
                LastDayWithData = sortedDates.Count > 0 ? sortedDates[^1] : null,
                DaysWithDataCount = sortedDates.Count,
                ByProvider = byProvider
            };
        }
    }

    public CostLedgerRangeResult GetRange(string? provider = null, DateOnly fromDay = default, DateOnly toDay = default)
    {
        if (!EnsureLoadStarted()) return new CostLedgerRangeResult();
        lock (_stateLock)
        {
            if (fromDay > toDay)
            {
                return new CostLedgerRangeResult();
            }

            var normalizedProvider = provider != null && ProviderConstants.IsValidProvider(provider)
                ? ProviderConstants.ValidateAndNormalize(provider)
                : null;

            var dayStatuses = new List<CostLedgerDayStatus>();
            decimal totalCost = 0;
            long totalTokens = 0;
            long inputTokens = 0;
            long outputTokens = 0;
            long cacheRead = 0;
            long cacheWrite = 0;
            long reasoning = 0;
            var daysWithDataCount = 0;

            for (var d = fromDay; d <= toDay; d = d.AddDays(1))
            {
                var dateKey = d.ToString("yyyy-MM-dd");

                if (normalizedProvider != null)
                {
                    if (_file.Providers.TryGetValue(normalizedProvider, out var pDays) &&
                        pDays.TryGetValue(dateKey, out var entry))
                    {
                        var status = new CostLedgerDayStatus
                        {
                            Date = d,
                            HasLedgerRecord = true,
                            CostUSD = entry.CostUSD,
                            TotalTokens = entry.TotalTokens,
                            InputTokens = entry.InputTokens,
                            OutputTokens = entry.OutputTokens,
                            CacheReadTokens = entry.CacheReadTokens,
                            CacheWriteTokens = entry.CacheWriteTokens,
                            ReasoningTokens = entry.ReasoningTokens,
                            Models = entry.Models ?? []
                        };
                        dayStatuses.Add(status);
                        if (status.HasData)
                        {
                            totalCost += status.CostUSD;
                            totalTokens += status.TotalTokens;
                            inputTokens += status.InputTokens;
                            outputTokens += status.OutputTokens;
                            cacheRead += status.CacheReadTokens;
                            cacheWrite += status.CacheWriteTokens;
                            reasoning += status.ReasoningTokens;
                            daysWithDataCount++;
                        }
                    }
                    else
                    {
                        dayStatuses.Add(new CostLedgerDayStatus
                        {
                            Date = d,
                            HasLedgerRecord = false
                        });
                    }
                }
                else
                {
                    // Aggregate across all providers for this day
                    var matchingEntries = new List<CostLedgerDayEntry>();
                    foreach (var pDays in _file.Providers.Values)
                    {
                        if (pDays.TryGetValue(dateKey, out var entry))
                        {
                            matchingEntries.Add(entry);
                        }
                    }

                    if (matchingEntries.Count > 0)
                    {
                        var dayCost = matchingEntries.Sum(e => e.CostUSD);
                        var dayTokens = matchingEntries.Sum(e => e.TotalTokens);
                        var dayInput = matchingEntries.Sum(e => e.InputTokens);
                        var dayOutput = matchingEntries.Sum(e => e.OutputTokens);
                        var dayCacheRead = matchingEntries.Sum(e => e.CacheReadTokens);
                        var dayCacheWrite = matchingEntries.Sum(e => e.CacheWriteTokens);
                        var dayReasoning = matchingEntries.Sum(e => e.ReasoningTokens);

                        var modelsMap = new Dictionary<string, (decimal Cost, long Tokens)>(StringComparer.OrdinalIgnoreCase);
                        foreach (var e in matchingEntries)
                        {
                            foreach (var m in e.Models ?? [])
                            {
                                if (string.IsNullOrWhiteSpace(m.ModelName)) continue;
                                if (modelsMap.TryGetValue(m.ModelName, out var existingM))
                                {
                                    modelsMap[m.ModelName] = (existingM.Cost + (m.CostUSD ?? 0), existingM.Tokens + m.TotalTokens);
                                }
                                else
                                {
                                    modelsMap[m.ModelName] = (m.CostUSD ?? 0, m.TotalTokens);
                                }
                            }
                        }

                        var status = new CostLedgerDayStatus
                        {
                            Date = d,
                            HasLedgerRecord = true,
                            CostUSD = dayCost,
                            TotalTokens = dayTokens,
                            InputTokens = dayInput,
                            OutputTokens = dayOutput,
                            CacheReadTokens = dayCacheRead,
                            CacheWriteTokens = dayCacheWrite,
                            ReasoningTokens = dayReasoning,
                            Models = modelsMap.Select(kv => new CostLedgerModelEntry
                            {
                                ModelName = kv.Key,
                                CostUSD = kv.Value.Cost,
                                TotalTokens = kv.Value.Tokens
                            }).OrderByDescending(m => m.CostUSD ?? 0).ToArray()
                        };
                        dayStatuses.Add(status);
                        if (status.HasData)
                        {
                            totalCost += status.CostUSD;
                            totalTokens += status.TotalTokens;
                            inputTokens += status.InputTokens;
                            outputTokens += status.OutputTokens;
                            cacheRead += status.CacheReadTokens;
                            cacheWrite += status.CacheWriteTokens;
                            reasoning += status.ReasoningTokens;
                            daysWithDataCount++;
                        }
                    }
                    else
                    {
                        dayStatuses.Add(new CostLedgerDayStatus
                        {
                            Date = d,
                            HasLedgerRecord = false
                        });
                    }
                }
            }

            return new CostLedgerRangeResult
            {
                TotalCostUSD = totalCost,
                TotalTokens = totalTokens,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CacheReadTokens = cacheRead,
                CacheWriteTokens = cacheWrite,
                ReasoningTokens = reasoning,
                DaysWithDataCount = daysWithDataCount,
                Days = dayStatuses
            };
        }
    }

    public CostLedgerLast30DaysResult GetLast30Days(string? provider = null, DateOnly? asOfDay = null)
    {
        var end = asOfDay ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_clock.GetUtcNow().UtcDateTime, _clock.LocalTimeZone));
        var start = end.AddDays(-29);

        var range = GetRange(provider, start, end);

        return new CostLedgerLast30DaysResult
        {
            TotalCostUSD = range.TotalCostUSD,
            TotalTokens = range.TotalTokens,
            InputTokens = range.InputTokens,
            OutputTokens = range.OutputTokens,
            CacheReadTokens = range.CacheReadTokens,
            CacheWriteTokens = range.CacheWriteTokens,
            ReasoningTokens = range.ReasoningTokens,
            DaysWithDataCount = range.DaysWithDataCount,
            Days = range.Days
        };
    }
}
