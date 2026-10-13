using Windows.System.Power;

namespace CodexBarWin.Services;

public sealed class WindowsBatterySaverStatusSource : IBatterySaverStatusSource
{
    public int EnergySaverStatus => (int)PowerManager.EnergySaverStatus;

    public event EventHandler<object>? EnergySaverStatusChanged
    {
        add => PowerManager.EnergySaverStatusChanged += value;
        remove => PowerManager.EnergySaverStatusChanged -= value;
    }
}
