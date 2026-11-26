namespace AgentsWinBar.Services;

public interface IBatterySaverMonitor
{
    bool BatterySaverActive { get; }
    event EventHandler? BatterySaverChanged;
}

/// <summary>Injectable boundary around the Windows power API.</summary>
public interface IBatterySaverStatusSource
{
    int EnergySaverStatus { get; }
    event EventHandler<object>? EnergySaverStatusChanged;
}
