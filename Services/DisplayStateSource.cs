using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Follows the console display state (on, off, dimmed) through a power-setting callback. The callback is a plain native
/// one, so it does not depend on a UI synchronization context (the SystemEvents session events never reached the app).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DisplayStateSource : IDisplayStateSource, IDisposable
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PbtPowerSettingChange = 0x8013;
    private const int SettingDataOffset = 20;   // POWERBROADCAST_SETTING: Guid (16) + DataLength (4) + Data

    private static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    [StructLayout(LayoutKind.Sequential)]
    private struct SubscribeParameters
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    private delegate uint NotifyCallback(IntPtr context, uint type, IntPtr setting);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags,
        ref SubscribeParameters recipient, out IntPtr handle);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingUnregisterNotification(IntPtr handle);

    private readonly ILogger<DisplayStateSource> _logger;
    private readonly NotifyCallback _callback;   // kept alive for as long as the registration
    private IntPtr _registration;
    private volatile bool _isOn = true;
    private volatile bool _disposed;

    public bool IsOn => _isOn;

    public event EventHandler? Changed;

    public DisplayStateSource(ILogger<DisplayStateSource> logger) : this(logger, registerNotifications: true)
    {
    }

    /// <summary>Tests pass false: the real notification reports the machine's actual display state at any moment.</summary>
    internal DisplayStateSource(ILogger<DisplayStateSource> logger, bool registerNotifications)
    {
        _logger = logger;
        _callback = OnNotify;
        if (!registerNotifications) return;
        try
        {
            var parameters = new SubscribeParameters { Callback = Marshal.GetFunctionPointerForDelegate(_callback) };
            var setting = ConsoleDisplayState;
            var result = PowerSettingRegisterNotification(ref setting, DeviceNotifyCallback, ref parameters, out _registration);
            if (result != 0) _logger.LogWarning("Display state notifications unavailable (error {Error})", result);
        }
        catch (Exception ex)
        {
            // Without the notification the display is simply assumed to be on, as before.
            _logger.LogWarning(ex, "Display state notifications unavailable");
        }
    }

    private uint OnNotify(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            if (_disposed || type != PbtPowerSettingChange || setting == IntPtr.Zero) return 0;
            if (Marshal.PtrToStructure<Guid>(setting) != ConsoleDisplayState) return 0;
            if (Marshal.ReadInt32(setting, 16) < sizeof(int)) return 0;
            Apply(unchecked((uint)Marshal.ReadInt32(setting, SettingDataOffset)));
        }
        catch (Exception ex) { _logger.LogError(ex, "Display state callback failed"); }
        return 0;
    }

    /// <summary>Applies a GUID_CONSOLE_DISPLAY_STATE value: 0 off, 1 on, 2 dimmed.</summary>
    internal void Apply(uint state)
    {
        var on = ParseIsOn(state);
        if (_isOn == on) return;
        _isOn = on;
        _logger.LogInformation("Display state: {State}", on ? "on" : "off");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Only an explicit "off" counts as off: dimmed and unknown values still show the screen.</summary>
    internal static bool ParseIsOn(uint state) => state != 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registration != IntPtr.Zero)
        {
            PowerSettingUnregisterNotification(_registration);
            _registration = IntPtr.Zero;
        }
    }
}
