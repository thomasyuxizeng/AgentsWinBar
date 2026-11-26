namespace AgentsWinBar.Helpers;

/// <summary>The one place the usage colour thresholds live: amber from <see cref="Warning"/>, red from <see cref="Critical"/>.</summary>
public static class UsageThresholds
{
    public const double Warning = 75;
    public const double Critical = 90;

    /// <summary>0 = normal, 1 = warning (amber), 2 = critical (red).</summary>
    public static int Level(double percent) => percent >= Critical ? 2 : percent >= Warning ? 1 : 0;
}
