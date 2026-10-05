using System.Runtime.InteropServices;

namespace CodexBarWin.Helpers;

/// <summary>Releases idle process working-set pages without affecting application state.</summary>
public static class WorkingSetTrimmer
{
    public static bool ShouldTrim(DateTime now, DateTime? lastTrim, bool flyoutVisible,
        bool dashboardOpen, bool refreshRunning) =>
        !flyoutVisible && !dashboardOpen && !refreshRunning &&
        (!lastTrim.HasValue || now - lastTrim.Value >= TimeSpan.FromSeconds(60));

    public static void Trim()
    {
        try
        {
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
