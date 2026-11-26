using System.Runtime.InteropServices;

namespace AgentsWinBar.Helpers;

/// <summary>
/// Calls back when the taskbar or the displays may have moved: work-area and other setting changes (WM_SETTINGCHANGE),
/// display mode changes (WM_DISPLAYCHANGE), DPI changes of the window (WM_DPICHANGED) and an Explorer restart
/// ("TaskbarCreated"). Windows broadcasts these to every top-level window, so the watcher subclasses the given window.
/// Create and dispose it on the window's UI thread.
/// </summary>
internal sealed class TaskbarChangeWatcher : IDisposable
{
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_DPICHANGED = 0x02E0;
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private static nuint _nextId = 0x43425754;   // "CBWT"

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, nuint idSubclass, nuint refData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, nuint idSubclass, nuint refData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, nuint idSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    private readonly IntPtr _hwnd;
    private readonly nuint _id;
    private readonly Action _onChange;
    private SubclassProc? _proc;   // kept alive while the subclass is installed

    /// <summary>False when the subclass could not be installed (the caller then has to poll).</summary>
    public bool IsActive => _proc != null;

    public TaskbarChangeWatcher(IntPtr hwnd, Action onChange)
    {
        _hwnd = hwnd;
        _onChange = onChange;
        _id = _nextId++;
        var proc = new SubclassProc(WndProc);
        if (SetWindowSubclass(hwnd, proc, _id, 0)) _proc = proc;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, nuint idSubclass, nuint refData)
    {
        if (msg is WM_SETTINGCHANGE or WM_DISPLAYCHANGE or WM_DPICHANGED || (msg == TaskbarCreated && msg != 0))
        {
            try { _onChange(); }
            catch { /* a window procedure must not throw; the next poll catches up */ }
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_proc == null) return;
        RemoveWindowSubclass(_hwnd, _proc, _id);
        _proc = null;
    }
}
