namespace CodexBarWin.Models;

/// <summary>
/// Where a provider's usage numbers come from: read natively over HTTPS with the Windows sign-ins ("native", the
/// default) or through the CodexBar CLI inside WSL ("wsl", kept as a fallback).
/// </summary>
/// <remarks>The same two values select the cost source (<see cref="AppSettings.CostSource"/>).</remarks>
public static class UsageSource
{
    public const string Native = "native";
    public const string Wsl = "wsl";

    /// <summary>Lowercases and trims a stored value; anything other than "wsl" becomes "native".</summary>
    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Wsl, StringComparison.OrdinalIgnoreCase) ? Wsl : Native;

    /// <summary>One "native" entry per allowed provider, in a stable order.</summary>
    public static Dictionary<string, string> Defaults() =>
        ProviderConstants.AllowedProviders
            .Select(ProviderConstants.ValidateAndNormalize)
            .Order(StringComparer.Ordinal)
            .ToDictionary(p => p, _ => Native, StringComparer.Ordinal);
}
