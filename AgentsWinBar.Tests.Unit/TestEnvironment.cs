namespace AgentsWinBar.Tests.Unit;

/// <summary>
/// Test switches read from the environment. Each switch is honored under the current name (AGENTSWINBAR_*) and under
/// the name used before the rename (CODEXBARWIN_*), so an existing script that sets the old name keeps working.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>True when AGENTSWINBAR_{name} or CODEXBARWIN_{name} is "1".</summary>
    public static bool IsSet(string name) =>
        Environment.GetEnvironmentVariable("AGENTSWINBAR_" + name) == "1" ||
        Environment.GetEnvironmentVariable("CODEXBARWIN_" + name) == "1";
}
