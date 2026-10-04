namespace CodexBarWin.Helpers;

/// <summary>
/// Short display names for the extra usage windows the CLI reports. Titles reach every screen (flyout cards,
/// compact displays, detail pages, the dashboard, alerts), and the flyout shares its width between the label
/// and the bar, so shorter labels leave more room for the bar.
/// </summary>
public static class WindowTitles
{
    /// <summary>
    /// "Fable only" becomes "Fable"; "Claude and GPT 5-hour" and "Claude and GPT weekly" (Antigravity's pool
    /// for external models) become "External 5h" and "External 7d". Any other title is returned trimmed and unchanged.
    /// </summary>
    public static string Short(string? title)
    {
        var text = (title ?? string.Empty).Trim();
        if (text.Length == 0) return text;

        text = text.Replace("Claude and GPT", "External", StringComparison.OrdinalIgnoreCase);
        if (text.EndsWith(" only", StringComparison.OrdinalIgnoreCase)) text = text[..^5];
        if (text.EndsWith(" 5-hour", StringComparison.OrdinalIgnoreCase)) text = text[..^7] + " 5h";
        else if (text.EndsWith(" weekly", StringComparison.OrdinalIgnoreCase)) text = text[..^7] + " 7d";
        return text;
    }
}
