namespace CodexBarWin.Services;

/// <summary>Reports workstation session and resume transitions.</summary>
public interface ISessionStateSource
{
    /// <summary>True only when the current Windows session is known to be locked.</summary>
    bool IsLocked { get; }

    event EventHandler? Locked;
    event EventHandler? Unlocked;
    event EventHandler? Resumed;
}
