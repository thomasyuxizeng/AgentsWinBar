namespace AgentsWinBar.Helpers;

/// <summary>
/// Which provider's models an extra usage window belongs to; decides the colour of its bar.
/// </summary>
public enum WindowAccent
{
    /// <summary>A main window (session/weekly): the standard blue bar.</summary>
    None,

    /// <summary>Anthropic / Claude models (e.g. "Fable only"): faded Anthropic orange.</summary>
    Claude,

    /// <summary>OpenAI / GPT models: teal.</summary>
    OpenAi,

    /// <summary>A pool shared by Claude and GPT models (e.g. Antigravity's Claude and GPT quota): orange to teal gradient.</summary>
    ClaudeAndOpenAi,

    /// <summary>Anything else: a subtle teal.</summary>
    Other
}

public static class WindowAccents
{
    private static readonly string[] ClaudeWords = ["claude", "fable", "sonnet", "opus", "haiku", "anthropic"];
    private static readonly string[] OpenAiWords = ["gpt", "openai", "codex"];

    /// <summary>
    /// Works out the accent from a window's title ("Fable only", "Claude and GPT weekly", ...).
    /// A null or empty title means a main window.
    /// </summary>
    public static WindowAccent FromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return WindowAccent.None;
        }

        var lower = title.ToLowerInvariant();

        // "External 5h" / "External 7d" is Antigravity's shared Claude and GPT pool (see WindowTitles.Short)
        if (lower.StartsWith("external", StringComparison.Ordinal))
        {
            return WindowAccent.ClaudeAndOpenAi;
        }

        var claude = ClaudeWords.Any(lower.Contains);
        var openAi = OpenAiWords.Any(lower.Contains);

        return (claude, openAi) switch
        {
            (true, true) => WindowAccent.ClaudeAndOpenAi,
            (true, false) => WindowAccent.Claude,
            (false, true) => WindowAccent.OpenAi,
            _ => WindowAccent.Other
        };
    }
}
