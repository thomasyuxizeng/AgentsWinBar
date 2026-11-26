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

    public static void KillTree(Process process)
    {
        lock (process)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // The process may not have started or may already have exited.
            }
        }
    }
}
