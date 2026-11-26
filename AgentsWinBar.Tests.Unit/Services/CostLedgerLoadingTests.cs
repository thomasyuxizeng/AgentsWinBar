using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using AgentsWinBar.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class CostLedgerLoadingTests
{
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Test UTC+9", TimeSpan.FromHours(9), "Test UTC+9", "Test UTC+9");
    }

    // Work runs on one thread and continuations can run only after the current callback returns.
    // Blocking that callback on IO (GetResult/Wait) therefore stalls the pump.
    private sealed class SingleThreadPump : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public SingleThreadPump()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var work in _queue.GetConsumingEnumerable()) work.Callback(work.State);
            }) { IsBackground = true };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public Task Run(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try { action(); done.SetResult(); }
                catch (Exception ex) { done.SetException(ex); }
            }, null);
            return done.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            if (_thread.Join(TimeSpan.FromSeconds(1))) _queue.Dispose();
        }
    }

    private string _directory = null!;
    private string LedgerPath => Path.Combine(_directory, "cost-ledger.json");
    private string CachePath => Path.Combine(_directory, "cost-cache.json");
    private readonly MockSettingsService _settings = new();
    private readonly Clock _clock = new();
    private readonly DateOnly _day = new(2026, 10, 10);

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cbw-ledger-loading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private CostLedger Ledger() => new(_settings, NullLogger<CostLedger>.Instance, _clock, LedgerPath, CachePath);

    private Task WriteLedgerAsync(decimal cost = 25m) => File.WriteAllTextAsync(LedgerPath,
        JsonSerializer.Serialize(new CostLedgerFile
        {
            Providers = new()
            {
                ["claude"] = new()
                {
                    [_day.ToString("yyyy-MM-dd")] = new() { Date = _day, CostUSD = cost, TotalTokens = 100 }
                }
            }
        }, AppJsonSerializerContext.Default.CostLedgerFile));

    private decimal Query(CostLedger ledger, int kind) => kind switch
    {
        0 => ledger.GetLifetime("claude").TotalCostUSD,
        1 => ledger.GetRange("claude", _day, _day).TotalCostUSD,
        _ => ledger.GetLast30Days("claude", _day).TotalCostUSD
    };

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Query_NeverLoadedOnSingleThreadContext_ReturnsPromptlyAndLaterSeesBackgroundLoad(int kind)
    {
        await WriteLedgerAsync();
        var ledger = Ledger();
        // Hold IO to make the test deterministic regardless of filesystem speed. Queries must
        // return no data while loading is queued, even when another operation owns the gate.
        var gate = (SemaphoreSlim)typeof(CostLedger).GetField("_ioGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ledger)!;
        await gate.WaitAsync();
        using var pump = new SingleThreadPump();
        try
        {
            await pump.Run(() =>
            {
                SynchronizationContext.Current.Should().BeSameAs(pump);
                for (var i = 0; i < 20; i++) Query(ledger, kind).Should().Be(0);
            }).WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally { gate.Release(); }

        // Do not call LoadAsync here: only the query is allowed to initiate loading.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (Query(ledger, kind) == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Query(ledger, kind).Should().Be(25m);
        await ledger.LoadAsync(); // Drain any queued IO before temporary-directory cleanup.
    }

    [TestMethod]
    public async Task LoadAsync_SingleThreadContextBlockedUntilCompletion_DoesNotCaptureContext()
    {
        await WriteLedgerAsync();
        var ledger = Ledger();
        var gate = (SemaphoreSlim)typeof(CostLedger).GetField("_ioGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ledger)!;
        await gate.WaitAsync();
        using var pump = new SingleThreadPump();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = pump.Run(() =>
        {
            var load = ledger.LoadAsync();
            started.SetResult();
            // This deliberately blocks the pump. Ledger continuations must use the thread pool.
            load.GetAwaiter().GetResult();
        });
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
        finally { gate.Release(); }
        await loading.WaitAsync(TimeSpan.FromSeconds(2));
        ledger.GetLifetime("claude").TotalCostUSD.Should().Be(25m);
    }

    [TestMethod]
    public async Task LoadAsync_DeveloperModeThenDisabled_LoadsOnceAndRepeatedCallsPreserveMemory()
    {
        await WriteLedgerAsync();
        var ledger = Ledger();
        _settings.Settings.DeveloperModeEnabled = true;
        await ledger.LoadAsync();
        ledger.GetLifetime("claude").ByProvider.Should().BeEmpty();
        _settings.Settings.DeveloperModeEnabled = false;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => ledger.LoadAsync()));
        ledger.GetLifetime("claude").TotalCostUSD.Should().Be(25m);
        await WriteLedgerAsync(999m);
        await ledger.LoadAsync();
        ledger.GetLifetime("claude").TotalCostUSD.Should().Be(25m);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LoadAsync_BackfillWithoutDaily_UsesOriginalSuccessfulLocalDay(bool hasLastSuccess)
    {
        var fetched = new DateTime(2026, 10, 8, 16, 0, 0, DateTimeKind.Utc); // Oct 9 locally.
        var lastSuccess = fetched.AddDays(-1); // Oct 8 locally; must win over fetched time.
        var data = new CostData
        {
            Provider = "claude", TodayCostUSD = 12m, TodayTokens = 120,
            FetchedAt = fetched, LastSuccessfulFetch = hasLastSuccess ? lastSuccess : null,
            Daily = []
        };
        await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(new CostCacheFile
        {
            Items = new() { ["claude"] = data }
        }, AppJsonSerializerContext.Default.CostCacheFile));
        var ledger = Ledger();
        await ledger.LoadAsync();
        var originalDay = new DateOnly(2026, 10, hasLastSuccess ? 8 : 9);
        ledger.GetRange("claude", originalDay, originalDay).TotalCostUSD.Should().Be(12m);
        ledger.GetRange("claude", new(2026, 10, 11), new(2026, 10, 11)).Days.Single().HasLedgerRecord.Should().BeFalse();

        // A later scan with identical token coverage can reprice the past day downward.
        await ledger.RecordScanAsync(new CostData
        {
            Provider = "claude", FetchOutcome = FetchOutcome.Success,
            Daily = [new() { Date = originalDay, TotalCost = 5m, TotalTokens = 120 }]
        });
        ledger.GetRange("claude", originalDay, originalDay).TotalCostUSD.Should().Be(5m);
        var reloaded = Ledger();
        await reloaded.LoadAsync();
        reloaded.GetLifetime("claude").TotalCostUSD.Should().Be(5m);
    }
}
