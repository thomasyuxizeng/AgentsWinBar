using System.Runtime.InteropServices;

namespace AgentsWinBar.Helpers;

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

    private const long WS_EX_TOPMOST = 0x00000008;
    private const uint GW_HWNDPREV = 3;
    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeIsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>Plain Win32 visibility query avoiding WinRT AppWindow.IsVisible COM calls.</summary>
    public static bool IsWindowVisible(IntPtr hwnd) => hwnd != IntPtr.Zero && NativeIsWindowVisible(hwnd);

    /// <summary>
    /// Zero-allocation z-order check for whether <paramref name="hwnd"/> needs to be raised above overlapping visible
    /// uncloaked windows or lost its topmost flag. Safe to call from a background polling thread.
    /// </summary>
    public static bool NeedsRaise(IntPtr hwnd)
    {
        var isTopmost = (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
        if (!isTopmost || !TryGetWindowRect(hwnd, out var bounds) || bounds.IsEmpty) return true;

        var count = 0;
        for (var above = GetWindow(hwnd, GW_HWNDPREV); above != IntPtr.Zero; above = GetWindow(above, GW_HWNDPREV))
        {
            if (++count > ZOrderCoverage.MaxWindowsAbove) return true;
            if (!NativeIsWindowVisible(above)) continue;
            if (!TryGetWindowRect(above, out var aboveBounds)) continue;
            if (ZOrderCoverage.OverlapsVisible(true, aboveBounds, bounds) && !IsCloaked(above)) return true;
        }
        return false;
    }

    /// <summary>
    /// <see cref="AssertTopmost"/>, but only when it would change what is on screen (see <see cref="ZOrderCoverage"/>):
    /// the periodic re-raise of the strip and the overlay then costs a short z-order walk instead of a window move.
    /// Returns true when the window was raised.
    /// </summary>
    public static bool AssertTopmostIfCovered(IntPtr hwnd)
    {
        if (!NeedsRaise(hwnd)) return false;
        AssertTopmost(hwnd);
        return true;
    }

    /// <summary>The window's outer rectangle in physical screen pixels (what AppWindow.Position and Size report).</summary>
    public static bool TryGetWindowRect(IntPtr hwnd, out ScreenRect bounds)
    {
        if (GetWindowRect(hwnd, out var rect))
        {
            bounds = new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return true;
        }
        bounds = default;
        return false;
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

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

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;

    private const int DWMWCP_ROUNDSMALL = 3;

    /// <summary>
    /// Gives the window Windows 11's smaller corner radius (about half the default one).
    /// </summary>
    public static void UseSmallRoundedCorners(IntPtr hwnd)
    {
        var preference = DWMWCP_ROUNDSMALL;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    /// <summary>
    /// Makes the window's corners square (Windows 11), e.g. for a strip that sits flush against the taskbar.
    /// </summary>
    public static void DisableRoundedCorners(IntPtr hwnd)
    {
        var preference = DWMWCP_DONOTROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

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

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    // QUERY_USER_NOTIFICATION_STATE values that mean "a fullscreen app owns the screen"
    private const int QUNS_BUSY = 2;                    // fullscreen app (e.g. fullscreen video or game)
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3; // exclusive-mode Direct3D game
    private const int QUNS_PRESENTATION_MODE = 4;       // e.g. a slideshow

    /// <summary>
    /// True while a fullscreen game, video or presentation is in the foreground, when an always-on-top
    /// overlay would only get in the way.
    /// </summary>
    public static bool IsFullscreenAppActive()
    {
        return SHQueryUserNotificationState(out var state) == 0
            && state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE
            && FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
    }

    private const uint ABM_GETTASKBARPOS = 0x00000005;

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public AppBarRect rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);

    /// <summary>
    /// Screen edge the taskbar is docked to.
    /// </summary>
    public enum TaskbarEdge
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3
    }

    /// <summary>
    /// Position of the primary taskbar in physical screen pixels, or null if it can't be determined.
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom, TaskbarEdge Edge)? GetTaskbarBounds()
    {
        var data = new AppBarData { cbSize = (uint)Marshal.SizeOf<AppBarData>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == UIntPtr.Zero)
        {
            return null;
        }

        var edge = Enum.IsDefined(typeof(TaskbarEdge), (int)data.uEdge) ? (TaskbarEdge)data.uEdge : TaskbarEdge.Bottom;
        return (data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom, edge);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    /// <summary>
    /// The TaskbarAl value (Windows 11 taskbar alignment: 0 = left, 1 = center), or null when it is not set or cannot be
    /// read. A registry read: call it when the taskbar or the settings change, not on every tick.
    /// </summary>
    public static int? ReadTaskbarAlignment()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("TaskbarAl") is int value ? value : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the rectangles of the primary taskbar's Start button (class "Start") and system tray (class
    /// "TrayNotifyWnd") with plain Win32 calls. The window handles are cached and looked up again only when
    /// IsWindow says one is gone (for example after an Explorer restart). Thread-safe.
    /// </summary>
    public sealed class TaskbarParts
    {
        private const string TrayClass = "Shell_TrayWnd";
        private const string StartClass = "Start";
        private const string NotifyClass = "TrayNotifyWnd";

        private readonly object _lock = new();
        private readonly char[] _classBuffer = new char[64];
        private readonly EnumWindowsProc _enumProc;
        private string _searchClass = string.Empty;
        private IntPtr _found;
        private IntPtr _start;
        private IntPtr _notify;

        public TaskbarParts()
        {
            // One delegate for the object's life: the descendant search allocates nothing per call
            _enumProc = MatchClass;
        }

        /// <summary>
        /// The current Start and tray rectangles (null when a window is missing or empty). A cached handle that
        /// IsWindow rejects is looked up again at once (once per lost handle: a failed lookup leaves no handle). A
        /// window that was not found is searched for again only with <paramref name="lookUpMissing"/>, so a check that
        /// runs on every tick never walks the taskbar's child windows for a window this Windows build does not have.
        /// </summary>
        public (ScreenRect? Start, ScreenRect? Tray) Read(bool lookUpMissing)
        {
            lock (_lock)
            {
                var findStart = NeedsLookup(_start, lookUpMissing);
                var findNotify = NeedsLookup(_notify, lookUpMissing);
                if (findStart || findNotify)
                {
                    var taskbar = FindWindow(TrayClass, null);
                    if (findStart) _start = FindDescendant(taskbar, StartClass);
                    if (findNotify) _notify = FindDescendant(taskbar, NotifyClass);
                }
                return (RectOf(_start), RectOf(_notify));
            }
        }

        private static bool NeedsLookup(IntPtr hwnd, bool lookUpMissing) =>
            hwnd == IntPtr.Zero ? lookUpMissing : !IsWindow(hwnd);

        private static ScreenRect? RectOf(IntPtr hwnd) =>
            hwnd != IntPtr.Zero && TryGetWindowRect(hwnd, out var rect) && !rect.IsEmpty ? rect : null;

        /// <summary>A direct child first (the usual place), then any descendant.</summary>
        private IntPtr FindDescendant(IntPtr parent, string className)
        {
            if (parent == IntPtr.Zero) return IntPtr.Zero;
            var child = FindWindowEx(parent, IntPtr.Zero, className, null);
            if (child != IntPtr.Zero) return child;

            _searchClass = className;
            _found = IntPtr.Zero;
            EnumChildWindows(parent, _enumProc, IntPtr.Zero);
            return _found;
        }

        private bool MatchClass(IntPtr hwnd, IntPtr _)
        {
            var length = GetClassName(hwnd, _classBuffer, _classBuffer.Length);
            if (length == _searchClass.Length && _searchClass.AsSpan().SequenceEqual(_classBuffer.AsSpan(0, length)))
            {
                _found = hwnd;
                return false;
            }
            return true;
        }
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
    /// Hides the window from Alt+Tab and the taskbar switcher, falling back to <c>WS_EX_TOOLWINDOW</c> when
    /// <see cref="Microsoft.UI.Windowing.AppWindow.IsShownInSwitchers"/> is not supported (e.g. unpackaged runs).
    /// </summary>
    public static void HideFromSwitchers(Microsoft.UI.Windowing.AppWindow appWindow, IntPtr hwnd)
    {
        try
        {
            appWindow.IsShownInSwitchers = false;
        }
        catch (Exception ex) when (ex is NotImplementedException or COMException)
        {
            var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            style |= WS_EX_TOOLWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
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
