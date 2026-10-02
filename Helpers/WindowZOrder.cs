using System.Runtime.InteropServices;

namespace CodexBarWin.Helpers;

/// <summary>
/// Win32 helpers for keeping tray/overlay windows above other programs.
/// </summary>
internal static class WindowZOrder
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    public delegate void WinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    /// <summary>
    /// Moves the window to the top of the topmost band without activating it.
    /// Needed because other topmost windows can end up above ours.
    /// </summary>
    public static void AssertTopmost(IntPtr hwnd)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Brings the window to the foreground even when another app owns the foreground.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        AssertTopmost(hwnd);

        var foreground = GetForegroundWindow();
        if (foreground == hwnd)
        {
            return;
        }

        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    private const int GWL_STYLE = -16;
    private const long WS_THICKFRAME = 0x00040000;
    private const long WS_SYSMENU = 0x00080000;
    private const long WS_DLGFRAME = 0x00400000;
    private const long WS_BORDER = 0x00800000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>
    /// Removes the system frame from a borderless window. Even with no title bar, WinUI leaves the
    /// dialog-frame style bit set, which draws a white outline whose corners don't match the
    /// rounded window shape. Also clears the DWM border colour (Windows 11).
    /// </summary>
    public static void RemoveSystemBorder(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_DLGFRAME | WS_BORDER | WS_THICKFRAME | WS_SYSMENU);
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        var none = DWMWA_COLOR_NONE;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
    }

    public static IntPtr GetForeground() => GetForegroundWindow();

    /// <summary>
    /// Gives the foreground back to a previously active window (no z-order changes).
    /// </summary>
    public static void RestoreForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || GetForegroundWindow() == hwnd)
        {
            return;
        }

        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var attached = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    /// <summary>
    /// Hides the window from Alt+Tab/taskbar and stops it from taking focus when clicked.
    /// </summary>
    public static void MakeNonActivatingToolWindow(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>
    /// Starts a native window drag (as if the title bar was grabbed). Blocks until the drag ends.
    /// </summary>
    public static void BeginDrag(IntPtr hwnd)
    {
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
    }

    public static double GetScale(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>
    /// Raises <paramref name="onForegroundChanged"/> whenever the foreground window changes.
    /// Must be created on a thread with a message loop (the UI thread).
    /// </summary>
    public sealed class ForegroundWatcher : IDisposable
    {
        private readonly WinEventProc _proc;
        private IntPtr _hook;

        public ForegroundWatcher(Action onForegroundChanged)
        {
            // Keep the delegate alive for as long as the hook is installed.
            _proc = (_, _, _, _, _, _, _) => onForegroundChanged();
            _hook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}
