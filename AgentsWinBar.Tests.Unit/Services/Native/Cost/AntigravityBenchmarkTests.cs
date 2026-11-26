using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using AgentsWinBar.Services.Native;
using AgentsWinBar.Services.Native.Cost;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentsWinBar.Tests.Unit.Services.Native.Cost;

/// <summary>
/// An opt-in measurement of a scan of the Antigravity databases under one Gemini home, skipped unless
/// <c>AGENTSWINBAR_BENCH_GEMINI_HOME</c> names that folder (a <c>.gemini</c> folder). The index goes to a temporary
/// folder that is deleted afterwards. Only aggregates are printed: counts, per-model token totals and estimated cost,
/// CPU time and peak private memory. No path, id or text from a conversation is printed.
/// </summary>
[TestClass]
public sealed partial class AntigravityBenchmarkTests
{
    internal const string HomeVariable = "AGENTSWINBAR_BENCH_GEMINI_HOME";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Benchmark")]
    public void Update_WhenBenchmarkHomeIsSet_PrintsAggregatesOnly()
    {
        var home = Environment.GetEnvironmentVariable(HomeVariable)?.Trim();
        if (string.IsNullOrEmpty(home) || !Path.IsPathFullyQualified(home) || !Directory.Exists(home))
            Assert.Inconclusive($"Set {HomeVariable} to a .gemini folder to measure a scan of its Antigravity databases.");

        var indexDirectory = CostTestSupport.TempDirectory("agy-bench");
        try
        {
            var roots = NativeCostService.GeminiRoots(home);
            var scanner = new NativeCostScanner(indexDirectory, NullLogger<NativeCostScanner>.Instance, TimeZoneInfo.Local);
            // As the service runs it: two readers for a full listing, one for changed files.
            var first = Measure(() => scanner.Update(roots, workers: 2));
            var listing = Measure(() => scanner.Update(roots, workers: 2));
            var notified = Measure(() => scanner.Update(roots, changedPaths: []));
            var report = scanner.ReadReport(CostProvider.Gemini, new CostPricing(), DateTimeOffset.Now, historyDays: 3650);

            var gemini = first.Result.Gemini ?? new AntigravityScanStatistics();
            Print($"databases scanned: {gemini.Databases} ({gemini.HistoryDatabases} with history), unreadable: {first.Result.UnavailableFiles}");
            Print($"gen_metadata rows: {gemini.Rows}, invalid: {gemini.InvalidRows}, calls: {gemini.Calls}");
            Print($"calls dated by own time: {gemini.DatedByOwnTime}, by bot id: {gemini.DatedByBotId}, " +
                  $"by position: {gemini.DatedByPosition}, undated: {gemini.Undated}");
            Print($"bytes in scanned databases and logs: {first.Result.BytesRead / 1_048_576.0:F1} MiB");
            foreach (var model in report.Daily!.SelectMany(day => day.ModelBreakdowns)
                         .GroupBy(model => model.ModelName)
                         .Select(group => (Name: group.Key, Tokens: group.Sum(m => m.TotalTokens),
                             Cost: group.All(m => m.Cost is not null) ? group.Sum(m => m.Cost ?? 0) : (decimal?)null))
                         .OrderByDescending(model => model.Tokens))
                Print($"model {model.Name}: {model.Tokens} tokens, {(model.Cost is { } cost ? cost.ToString("F2", CultureInfo.InvariantCulture) + " USD" : "unpriced")}");
            Print($"all days: {report.Daily!.Sum(day => day.TotalTokens)} tokens, " +
                  $"{report.Daily!.Sum(day => day.TotalCost).ToString("F2", CultureInfo.InvariantCulture)} USD estimated, " +
                  $"{report.UnpricedTokens} unpriced tokens");
            Print($"first full scan: {first.CpuSeconds:F2} CPU-s, {first.WallSeconds:F2} s, peak extra private memory {first.PeakExtraMiB:F1} MiB");
            Print($"unchanged full listing: {listing.CpuSeconds:F3} CPU-s, {listing.WallSeconds:F3} s, read {listing.Result.FilesRead} databases");
            Print($"unchanged update without notifications: {notified.CpuSeconds:F3} CPU-s, read {notified.Result.FilesRead} databases");
        }
        finally
        {
            Directory.Delete(indexDirectory, recursive: true);
        }
    }

    private void Print(string line) => TestContext.WriteLine(line);

    private sealed record Measurement(CostScanResult Result, double CpuSeconds, double WallSeconds, double PeakExtraMiB);

    /// <summary>Process CPU time and the peak of private bytes above the starting level, sampled every 5 ms.</summary>
    private static Measurement Measure(Func<CostScanResult> scan)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = PrivateBytes();
        var peak = baseline;
        using var stop = new ManualResetEventSlim();
        var sampler = new Thread(() =>
        {
            while (!stop.Wait(5)) peak = Math.Max(peak, PrivateBytes());
        }) { IsBackground = true };
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        sampler.Start();
        CostScanResult result;
        try
        {
            result = scan();
        }
        finally
        {
            stop.Set();
            sampler.Join();
        }
        wall.Stop();
        process.Refresh();
        peak = Math.Max(peak, PrivateBytes());
        return new(result, (process.TotalProcessorTime - cpu).TotalSeconds, wall.Elapsed.TotalSeconds,
            (peak - baseline) / 1_048_576.0);
    }

    private static long PrivateBytes()
    {
        var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        return GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size) ? (long)counters.PrivateUsage : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        internal uint Size;
        internal uint PageFaultCount;
        internal nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
        internal nuint QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
}
