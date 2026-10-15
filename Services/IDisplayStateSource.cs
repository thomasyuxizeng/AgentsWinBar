namespace CodexBarWin.Services;

/// <summary>Reports whether the display is on (the screen can be seen) and when that changes.</summary>
public interface IDisplayStateSource
{
    /// <summary>False only while Windows reports the display as turned off; true when on, dimmed or unknown.</summary>
    bool IsOn { get; }

    /// <summary>Raised when <see cref="IsOn"/> changes, on an arbitrary thread.</summary>
    event EventHandler? Changed;
}
