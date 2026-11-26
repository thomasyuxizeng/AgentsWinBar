namespace AgentsWinBar.Helpers;

/// <summary>
/// How a plan name is shown on a badge or detail row. Display only: the stored value and what the usage parsers return
/// stay as the provider reported them (Codex reports "team", "plus"; Claude and Gemini already send "Claude Max 20x",
/// "Google AI Pro").
/// </summary>
public static class PlanNameFormatter
{
    /// <summary>
    /// Title case for words that are entirely lowercase letters ("team" to "Team", "free workspace" to "Free Workspace",
    /// "free_workspace" to "Free Workspace"). Words with a capital, a digit or a symbol ("Max", "20x", "AI") are kept as
    /// they are, and nothing is ever lowercased. Null or blank gives null.
    /// </summary>
    public static string? Display(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return null;
        var trimmed = plan.Trim();
        // An identifier-style name ("free_workspace") reads as words; one that has a capital is left alone
        if (trimmed.Contains('_') && !trimmed.Any(char.IsUpper)) trimmed = trimmed.Replace('_', ' ');
        var words = trimmed.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word.Length > 0 && word.All(char.IsLower))
                words[i] = char.ToUpperInvariant(word[0]) + word[1..];
        }
        return string.Join(' ', words);
    }
}
