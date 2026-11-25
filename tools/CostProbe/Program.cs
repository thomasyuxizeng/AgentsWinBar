using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodexBarWin.Models;
using CodexBarWin.Services.Native.Cost;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "parity")
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            using var current = Process.GetCurrentProcess();
            current.PriorityClass = ProcessPriorityClass.BelowNormal;
            return Parity.Run(args, stop.Token);
        }

        if (args.Length != 2 || !int.TryParse(args[1], out var workers) || workers is < 1 or > 2)
        {
            Console.Error.WriteLine("Usage: CostProbe <fresh-index-directory> <1|2>");
            Console.Error.WriteLine("       CostProbe parity <from yyyy-MM-dd> <to yyyy-MM-dd> [index-directory]");
            return 2;
        }

        using var process = Process.GetCurrentProcess();
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            var directory = Path.GetFullPath(args[0]);
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Console.Error.WriteLine("Use an empty probe directory.");
                return 2;
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData = Path.Combine(localData, "Packages", "CodexBarWin_w05y65rx3mcz6", "LocalCache",
                "Local", "CodexBarWin");
            var synthetic = Path.Combine(directory, "synthetic");
            Directory.CreateDirectory(synthetic);
            var fixture = Path.Combine(synthetic, "burst.jsonl");
            File.WriteAllBytes(fixture, []);
            CostLogRoot[] roots =
            [
                new(CostProvider.Claude, Path.Combine(profile, ".claude", "projects")),
                new(CostProvider.Codex, Path.Combine(profile, ".codex", "sessions")),
                new(CostProvider.Claude, synthetic)
            ];

            var scanner = new NativeCostScanner(directory, NullLogger<NativeCostScanner>.Instance);
            var asOf = DateTimeOffset.Now;
            var today = DateOnly.FromDateTime(asOf.DateTime);
            Console.WriteLine($"Calendar: {TimeZoneInfo.Local.Id}, as of {today:yyyy-MM-dd}");
            Console.WriteLine("| Phase | Workers | Wall ms | CPU ms | Allocated MB | Peak managed MB | Peak private MB | Files opened | Bytes read |");
            Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            var full = Measure(process, "Full", workers, () => scanner.Update(roots, workers, cancellation.Token));
            CostScanResult idle;
            for (var attempt = 0; ; attempt++)
            {
                idle = Measure(process, "No change", 1, () => scanner.Update(roots, 1, cancellation.Token));
                if (idle.FilesRead == 0 || attempt == 4) break;
            }
            var appendedBytes = AppendBurst(fixture, asOf);
            var burst = Measure(process, "Append", 1, () => scanner.Update(roots, 1, cancellation.Token));
            Console.WriteLine($"Synthetic append: 100 lines, {appendedBytes} bytes. Index: {new FileInfo(Path.Combine(directory, "index.sqlite")).Length} bytes.");
            Console.WriteLine($"Discovered: {full.DiscoveredFiles} files, {full.DiscoveredBytes} bytes, including one empty fixture.");
            Console.WriteLine($"Malformed/oversized/disappeared, full: {full.MalformedLines}/{full.OversizedLines}/{full.UnavailableFiles}, " +
                $"no change: {idle.MalformedLines}/{idle.OversizedLines}/{idle.UnavailableFiles}, append: {burst.MalformedLines}/{burst.OversizedLines}/{burst.UnavailableFiles}.");
            ReportParity(scanner, appData, asOf, today, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Probe cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            // Exceptions from enumeration or decoding can contain private paths or values.
            Console.Error.WriteLine($"Probe failed ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
            return 1;
        }
    }

    private static long AppendBurst(string path, DateTimeOffset asOf)
    {
        var padding = new string('x', 1_024);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write);
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            for (var i = 0; i < 100; i++)
                writer.WriteLine($$$$"""
                    {"type":"assistant","timestamp":"{{{{asOf:O}}}}","requestId":"synthetic-{{{{i}}}}","message":{"id":"synthetic-message-{{{{i}}}}","model":"claude-sonnet-4-6","content":[{"type":"text","text":"{{{{padding}}}}"}],"usage":{"input_tokens":1000,"output_tokens":100,"cache_read_input_tokens":50}}}
                    """);
        }
        return stream.Length;
    }

    private static CostScanResult Measure(Process process, string label, int workers, Func<CostScanResult> scan)
    {
        using var stop = new ManualResetEventSlim();
        var peakManaged = GC.GetTotalMemory(false);
        var sampler = new Thread(() =>
        {
            while (!stop.Wait(5)) peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
        }) { IsBackground = true };
        sampler.Start();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        CostScanResult result;
        try { result = scan(); }
        finally
        {
            timer.Stop();
            stop.Set();
            sampler.Join();
        }
        var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
        peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false));
        var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        if (!GetProcessMemoryInfo(process.Handle, ref counters, counters.Size))
            throw new InvalidOperationException("Private memory counters are unavailable.");

        const decimal mb = 1_000_000m;
        if (label == "No change" && result.FilesRead > 0) label = "Live catch-up";
        Console.WriteLine($"| {label} | {workers} | {timer.Elapsed.TotalMilliseconds:F1} | {cpuMs:F1} | " +
            $"{allocatedBytes / mb:F2} | {peakManaged / mb:F2} | {(ulong)counters.PeakCommit / mb:F2} | {result.FilesRead} | {result.BytesRead} |");
        return result;
    }

    private static void ReportParity(NativeCostScanner scanner, string path, DateTimeOffset asOf, DateOnly today,
        CancellationToken token)
    {
        var ledger = Read<CostLedgerFile>(Path.Combine(path, "cost-ledger.json"));
        var cache = Read<CostCacheFile>(Path.Combine(path, "cost-cache.json"));
        Console.WriteLine($"CLI snapshots present: ledger={ledger is not null}, cache={cache is not null}.");
        Console.WriteLine("| Provider | Date | Native USD | CLI USD | Delta USD | Delta % | CLI source |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---|");
        foreach (var provider in Enum.GetValues<CostProvider>())
        {
            var report = scanner.ReadReport(provider, asOf: asOf, historyDays: 31, cancellationToken: token);
            var native = report.Daily!.ToDictionary(day => day.Date);
            var name = report.Provider;
            var ledgerDays = ledger?.Providers.GetValueOrDefault(name);
            var cacheDays = cache?.Items.GetValueOrDefault(name)?.Daily.ToDictionary(day => day.Date);
            for (var date = today.AddDays(-30); date < today; date = date.AddDays(1))
            {
                var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var nativeUsd = native.GetValueOrDefault(date)?.TotalCost ?? 0;
                var ledgerDay = ledgerDays?.GetValueOrDefault(key);
                var cacheDay = cacheDays?.GetValueOrDefault(date);
                var cli = ledgerDay?.CostUSD ?? cacheDay?.TotalCost;
                var delta = cli is null ? null : nativeUsd - cli;
                var percent = cli switch
                {
                    null => "—",
                    0 => delta == 0 ? "0.00%" : "undefined",
                    _ => $"{delta / cli * 100:+0.00;-0.00;0.00}%"
                };
                var source = ledgerDay is not null ? "Ledger" : cacheDay is not null ? "Cache" : "Missing";
                Console.WriteLine($"| {name} | {key} | {nativeUsd:F6} | {cli?.ToString("F6") ?? "—"} | " +
                    $"{delta?.ToString("+0.000000;-0.000000;0.000000") ?? "—"} | {percent} | {source} |");
            }
            foreach (var date in native.Keys.Where(date => date < today).Order())
            {
                var nativeDay = native[date];
                var unknown = nativeDay.ModelBreakdowns.Where(model => model.Cost is null).Sum(model => model.TotalTokens);
                var cliTokens = ledgerDays?.GetValueOrDefault(date.ToString("yyyy-MM-dd"))?.TotalTokens ??
                    cacheDays?.GetValueOrDefault(date)?.TotalTokens;
                Console.Error.WriteLine($"{name} {date:yyyy-MM-dd} tokens native={nativeDay.TotalTokens} cli={cliTokens} unpriced={unknown}");
            }
        }
    }

    private static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, Json);
    }

    // PeakPagefileUsage is the Windows lifetime peak private commit charge.
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        internal uint Size, PageFaultCount;
        internal nuint PeakWorkingSet, WorkingSet, PeakPaged, Paged, PeakNonPaged, NonPaged, Commit, PeakCommit, Private;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, uint size);
}
