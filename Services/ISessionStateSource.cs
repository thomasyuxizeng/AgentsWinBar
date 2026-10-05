namespace CodexBarWin.Services;

/// <summary>Reports workstation session and resume transitions.</summary>
public interface ISessionStateSource
{
    event EventHandler? Locked;
    event EventHandler? Unlocked;
    event EventHandler? Resumed;
}
