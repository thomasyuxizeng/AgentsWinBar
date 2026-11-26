using System.Runtime.InteropServices;

namespace AgentsWinBar.Services;

/// <summary>Queries the current process's Windows session without caching its state.</summary>
internal static class SessionStateNative
{
    private const int WTSSessionInfoEx = 25;
    private const int SessionFlagsOffset = 16;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId,
        int informationClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr buffer);

    internal static bool IsCurrentSessionLocked()
    {
        try
        {
            if (!ProcessIdToSessionId((uint)Environment.ProcessId, out var sessionId)) return false;
            var buffer = IntPtr.Zero;
            try
            {
                return WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTSSessionInfoEx,
                    out buffer, out var bytesReturned) && ReadIsLocked(buffer, bytesReturned);
            }
            finally
            {
                if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
            }
        }
        catch (Exception)
        {
            // Missing APIs, failed queries and unknown states must never report a lock.
            return false;
        }
    }

    internal static bool ReadIsLocked(IntPtr buffer, uint bytesReturned)
    {
        // WTSINFOEXW: Level at 0; the aligned LEVEL1 union begins at 8, with SessionFlags at 16.
        return buffer != IntPtr.Zero && bytesReturned >= SessionFlagsOffset + sizeof(int)
            && Marshal.ReadInt32(buffer) == 1
            && Marshal.ReadInt32(buffer, SessionFlagsOffset) == 0; // WTS_SESSIONSTATE_LOCK
    }
}
