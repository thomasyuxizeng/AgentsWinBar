using System.Runtime;
using System.Runtime.InteropServices;

namespace CodexBarWin.Helpers;

/// <summary>Releases idle process working-set pages without affecting application state.</summary>
public static class WorkingSetTrimmer
{
    public static readonly TimeSpan StartupOrDashboardCooldown = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan GrowthCompactCooldown = TimeSpan.FromMinutes(10);
    public const long GrowthThresholdBytes = 8 * 1024 * 1024;

    public static bool ShouldTrim(DateTime now, DateTime? lastTrim, bool flyoutVisible,
        bool dashboardOpen, bool refreshRunning) =>
        !flyoutVisible && !dashboardOpen && !refreshRunning &&
        (!lastTrim.HasValue || now - lastTrim.Value >= TimeSpan.FromSeconds(60));

    public static bool ShouldCompactLoh(
        DateTime now,
        DateTime? lastCompact,
        bool hasCompactedAfterStartup,
        bool dashboardJustClosed,
        long currentMemoryBytes,
        long baselineMemoryBytes)
    {
        if (!hasCompactedAfterStartup) return true;
        if (lastCompact.HasValue && now - lastCompact.Value < StartupOrDashboardCooldown) return false;
        if (dashboardJustClosed) return true;
        if (lastCompact.HasValue && now - lastCompact.Value < GrowthCompactCooldown) return false;
        return baselineMemoryBytes > 0 && currentMemoryBytes - baselineMemoryBytes >= GrowthThresholdBytes;
    }

    public static void Trim() => Trim(compactLoh: false);

    public static void Trim(bool compactLoh)
    {
        try
        {
            if (compactLoh)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // This optional optimization must never interrupt refresh or shutdown.
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);
}
