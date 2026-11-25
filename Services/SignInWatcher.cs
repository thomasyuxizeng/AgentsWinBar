using System.Runtime.InteropServices;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>Asks for a refresh after a sign-in button was used. <see cref="CredentialChanged"/> is false for the
/// periodic Gemini re-check, which only matters while the provider still shows as signed out.</summary>
public sealed record SignInRefreshRequest(string Provider, bool CredentialChanged);

/// <summary>Watches for the sign-in a provider's own tool writes after the user clicked "Sign in".</summary>
public interface ISignInWatcher
{
    /// <summary>Raised on a thread-pool thread when the app should refresh to pick up a new sign-in.</summary>
    event EventHandler<SignInRefreshRequest>? RefreshRequested;
    /// <summary>Starts (or restarts) watching for <paramref name="provider"/>'s new sign-in.</summary>
    void Begin(string provider);
    /// <summary>Stops watching <paramref name="provider"/> (it is signed in again, or the launch failed).</summary>
    void Cancel(string provider);
    bool IsPending(string provider);
}

/// <summary>The cheap local signals the watcher reads; injectable so tests drive them by hand.</summary>
public interface ISignInSignals
{
    DateTime UtcNow { get; }
    /// <summary>%USERPROFILE%.</summary>
    string UserProfile { get; }
    /// <summary>Last write time of a file (UTC), or null when it does not exist. Never reads the file.</summary>
    DateTime? FileStamp(string path);
    /// <summary>When a Windows Credential Manager entry was last written (UTC), or null when it does not exist.</summary>
    DateTime? CredentialStamp(string target);
    /// <summary>Calls <paramref name="changed"/> when the file is created, changed or replaced. Null when its folder is missing.</summary>
    IDisposable? WatchFile(string path, Action changed);
    /// <summary>A timer: first call after <paramref name="dueTime"/>, then every <paramref name="period"/> (or once with
    /// <see cref="Timeout.InfiniteTimeSpan"/>).</summary>
    IDisposable StartTimer(TimeSpan dueTime, TimeSpan period, Action tick);
}

/// <summary>
/// After "Sign in" is clicked, picks up the new sign-in without a restart and without network polling:
/// Claude and Codex: a file watcher on the credential file (plus a 10 s check of its timestamp in case the folder did
/// not exist yet); the first change asks for one refresh, 2 s later so the tool has finished writing.
/// Gemini: a 10 s check of the Credential Manager entry's timestamp, and, since the Antigravity app may keep its
/// sign-in elsewhere, one refresh a minute while the provider still shows signed out.
/// Watching ends after the first refresh for a changed sign-in, when <see cref="Cancel"/> is called, or after
/// <see cref="FileWindow"/> (Claude, Codex) / <see cref="GeminiWindow"/>. Nothing runs while no sign-in is pending.
/// </summary>
public sealed class SignInWatcher(ISignInSignals signals, ILogger<SignInWatcher> logger) : ISignInWatcher, IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan GeminiRecheckInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan FileWindow = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan GeminiWindow = TimeSpan.FromMinutes(5);
    internal const string GeminiCredentialTarget = "gemini:antigravity";

    private sealed class Pending
    {
        public required DateTime Deadline { get; init; }
        public required DateTime? StartStamp { get; init; }
        public DateTime NextRecheck { get; set; }
        public IDisposable? Watch { get; set; }
        public IDisposable? Settle { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private IDisposable? _poll;
    private bool _disposed;

    public event EventHandler<SignInRefreshRequest>? RefreshRequested;

    public bool IsPending(string provider)
    {
        var id = ProviderConstants.ValidateAndNormalize(provider);
        lock (_gate) return _pending.ContainsKey(id);
    }

    public void Begin(string provider)
    {
        var id = ProviderConstants.ValidateAndNormalize(provider);
        lock (_gate)
        {
            if (_disposed) return;
            End(id, keepPoll: true);
            var now = signals.UtcNow;
            var pending = new Pending
            {
                Deadline = now + (id == "gemini" ? GeminiWindow : FileWindow),
                StartStamp = Stamp(id),
                NextRecheck = now + GeminiRecheckInterval
            };
            _pending[id] = pending;
            if (CredentialFile(id) is { } path) pending.Watch = signals.WatchFile(path, () => OnFileChanged(id));
            _poll ??= signals.StartTimer(PollInterval, PollInterval, Poll);
        }
        logger.LogInformation("Watching for a new {Provider} sign-in", id);
    }

    public void Cancel(string provider)
    {
        if (!ProviderConstants.IsValidProvider(provider)) return;
        var id = ProviderConstants.ValidateAndNormalize(provider);
        lock (_gate)
        {
            if (End(id)) logger.LogDebug("Stopped watching for a new {Provider} sign-in", id);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var id in _pending.Keys.ToArray()) End(id);
        }
    }

    /// <summary>The credential file a file watcher can follow, or null for Gemini (Credential Manager).</summary>
    internal string? CredentialFile(string id) => id switch
    {
        "claude" => Path.Combine(signals.UserProfile, ".claude", ".credentials.json"),
        "codex" => Path.Combine(signals.UserProfile, ".codex", "auth.json"),
        _ => null
    };

    private DateTime? Stamp(string id) =>
        CredentialFile(id) is { } path ? signals.FileStamp(path) : signals.CredentialStamp(GeminiCredentialTarget);

    private void OnFileChanged(string id)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(id, out var pending) || pending.Settle != null) return;
            pending.Settle = signals.StartTimer(SettleDelay, Timeout.InfiniteTimeSpan, () => Fire(id, pending));
        }
    }

    /// <summary>The periodic check: deadlines, changed stamps and the Gemini re-check.</summary>
    internal void Poll()
    {
        var requests = new List<SignInRefreshRequest>();
        lock (_gate)
        {
            var now = signals.UtcNow;
            foreach (var (id, pending) in _pending.ToArray())
            {
                if (now >= pending.Deadline)
                {
                    End(id);
                    logger.LogInformation("No new {Provider} sign-in seen; stopped watching", id);
                }
                else if (pending.Settle == null && Stamp(id) is { } stamp && stamp != pending.StartStamp)
                {
                    End(id);
                    requests.Add(new(id, true));
                }
                else if (id == "gemini" && now >= pending.NextRecheck)
                {
                    pending.NextRecheck = now + GeminiRecheckInterval;
                    requests.Add(new(id, false));
                }
            }
        }
        foreach (var request in requests) Raise(request);
    }

    private void Fire(string id, Pending pending)
    {
        lock (_gate)
        {
            // A later Begin replaced this sign-in, or it already ended
            if (!_pending.TryGetValue(id, out var current) || !ReferenceEquals(current, pending)) return;
            End(id);
        }
        Raise(new(id, true));
    }

    private void Raise(SignInRefreshRequest request)
    {
        logger.LogInformation("Refreshing to pick up the new {Provider} sign-in (credential changed: {Changed})",
            request.Provider, request.CredentialChanged);
        try { RefreshRequested?.Invoke(this, request); }
        catch (Exception ex) { logger.LogWarning(ex, "Sign-in refresh request failed"); }
    }

    /// <summary>Stops watching <paramref name="id"/>; stops the poll timer when nothing is pending. Call under the lock.</summary>
    private bool End(string id, bool keepPoll = false)
    {
        if (!_pending.Remove(id, out var pending)) return false;
        pending.Watch?.Dispose();
        pending.Settle?.Dispose();
        if (_pending.Count == 0 && !keepPoll)
        {
            _poll?.Dispose();
            _poll = null;
        }
        return true;
    }
}

/// <summary>The real signals: file timestamps and watchers, the Credential Manager entry's LastWritten, thread-pool timers.</summary>
public sealed class WindowsSignInSignals : ISignInSignals
{
    public DateTime UtcNow => DateTime.UtcNow;
    public string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public DateTime? FileStamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Reads only the entry's LastWritten time; the secret blob CredReadW returns is never touched.</summary>
    public DateTime? CredentialStamp(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var handle)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<CredentialHeader>(handle);
            return DateTime.FromFileTimeUtc(credential.LastWritten);
        }
        catch (ArgumentOutOfRangeException) { return null; }
        finally { CredFree(handle); }
    }

    public IDisposable? WatchFile(string path, Action changed)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        var watcher = new FileSystemWatcher(folder, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = false
        };
        watcher.Changed += (_, _) => changed();
        watcher.Created += (_, _) => changed();
        watcher.Renamed += (_, _) => changed();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    public IDisposable StartTimer(TimeSpan dueTime, TimeSpan period, Action tick) =>
        new Timer(_ => tick(), null, dueTime, period);

    private const int CredTypeGeneric = 1;

    /// <summary>CREDENTIALW up to LastWritten; the rest of the structure (including the secret) is not mapped.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredentialHeader
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}
