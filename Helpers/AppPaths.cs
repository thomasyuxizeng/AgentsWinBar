namespace AgentsWinBar.Helpers;

/// <summary>
/// The app's data folder, named in one place. Packaged runs see %LOCALAPPDATA%\AgentsWinBar redirected into the
/// package's LocalCache\Local\AgentsWinBar; unpackaged runs use the real folder.
/// </summary>
public static class AppPaths
{
    /// <summary>Folder name under %LOCALAPPDATA% that holds settings, caches, history, the cost index and crash.log.</summary>
    public const string DataFolderName = "AgentsWinBar";

    /// <summary>The app's name before the rename. Its data is imported once (see LegacyDataImporter).</summary>
    public const string LegacyAppName = "CodexBarWin";

    /// <summary>%LOCALAPPDATA% (the real folder; packaged file access to it is redirected by Windows).</summary>
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>%LOCALAPPDATA%\AgentsWinBar.</summary>
    public static string DataDirectory => Path.Combine(LocalAppData, DataFolderName);

    /// <summary>A file in the data folder, for example <c>DataFile("settings.json")</c>.</summary>
    public static string DataFile(string fileName) => Path.Combine(DataDirectory, fileName);
}
