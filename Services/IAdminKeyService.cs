namespace AgentsWinBar.Services;

/// <summary>The outcome of checking an Anthropic Admin key.</summary>
public enum AdminKeyCheck
{
    /// <summary>Anthropic accepted the key (HTTP 2xx).</summary>
    Valid,
    /// <summary>Anthropic refused the key (HTTP 401 or 403).</summary>
    Rejected,
    /// <summary>No answer that says either way: network error, timeout, HTTP 429, 5xx or any other status.</summary>
    Unreachable,
    /// <summary>Not shaped like an Anthropic key (or no key at all), so nothing was sent.</summary>
    BadFormat
}

/// <summary>Whether an Admin key is set, and at most its last 4 characters for display (null when even that would reveal too much).</summary>
public sealed record AdminKeyStatus(bool IsSet, string? LastFour);

/// <summary>
/// Sets up the Anthropic Admin key that the API credits panel reads: the Windows user environment variable
/// <c>ANTHROPIC_ADMIN_KEY</c>. The key never leaves the service other than in the request to Anthropic and the
/// environment writes; callers see only <see cref="AdminKeyStatus"/> and <see cref="AdminKeyCheck"/>.
/// </summary>
public interface IAdminKeyService
{
    /// <summary>Raised (on a background thread) after a key was saved or removed.</summary>
    event EventHandler? KeyChanged;

    /// <summary>The key the app would use now (process environment first, then the user environment).</summary>
    AdminKeyStatus GetStatus();

    /// <summary>Checks a pasted key: the format first, then one read-only request to the Anthropic Admin API.</summary>
    Task<AdminKeyCheck> TestAsync(string? candidate, CancellationToken ct = default);

    /// <summary>Checks the key that is set now; <see cref="AdminKeyCheck.BadFormat"/> when none is set or it is not shaped like an Anthropic key.</summary>
    Task<AdminKeyCheck> TestSavedAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes a well-formed key (trimmed) to the user environment and to this process, off the calling thread.
    /// False when the format check fails (nothing is written) or a write failed.
    /// </summary>
    Task<bool> SaveAsync(string? candidate);

    /// <summary>Deletes the key from the user environment and from this process, off the calling thread. False when a write failed.</summary>
    Task<bool> RemoveAsync();
}
