using System.ComponentModel;
using System.Diagnostics;

namespace AgentsWinBar.Services.Native;

internal static class RenewalProcess
{
    // Cancellation must kill the tree before shutdown returns. Serialize it with Start so a child
    // cannot slip between the cancellation check and the process handle becoming available.
    public static CancellationTokenRegistration Start(Process process, CancellationToken shutdownToken)
    {
        var stopOnShutdown = shutdownToken.Register(() => KillTree(process));
        try
        {
            lock (process)
            {
                shutdownToken.ThrowIfCancellationRequested();
                if (!process.Start()) throw new InvalidOperationException("The sign-in renewal process did not start.");
            }
            return stopOnShutdown;
        }
        catch
        {
            stopOnShutdown.Dispose();
            throw;
        }
    }

    public static void KillTree(Process process) => KillTree(process, static p => p.Kill(entireProcessTree: true));

    /// <summary>
    /// Best effort: never throws for a process that is gone or a tree that cannot be fully stopped. It runs as a shutdown
    /// token callback, and a throwing callback would surface from the app's exit path.
    /// </summary>
    internal static void KillTree(Process process, Action<Process> kill)
    {
        lock (process)
        {
            try { kill(process); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
            {
                // The process may not have started or may already have exited. AggregateException: not every process
                // in its tree could be stopped.
            }
        }
    }
}
