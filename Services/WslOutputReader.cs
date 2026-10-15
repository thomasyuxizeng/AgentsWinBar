using System.Text;

namespace CodexBarWin.Services;

internal sealed class WslOutputLimitException() : IOException("WSL command output limit exceeded");

internal static class WslOutputReader
{
    internal const int CommandLimitBytes = 4 * 1024 * 1024;
    internal const int BatchLimitBytes = 32 * 1024 * 1024;

    internal sealed class Budget(int limit)
    {
        private int _remaining = limit;
        internal void Consume(int bytes)
        {
            if (Interlocked.Add(ref _remaining, -bytes) < 0) throw new WslOutputLimitException();
        }
    }

    internal static async Task<string> ReadToEndAsync(TextReader reader, Budget budget, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var encoder = Encoding.UTF8.GetEncoder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
        {
            budget.Consume(encoder.GetByteCount(buffer, 0, read, flush: false));
            text.Append(buffer, 0, read);
        }
        budget.Consume(encoder.GetByteCount([], 0, 0, flush: true));
        return text.ToString();
    }

    // Bound lines before allocating a whole frame or splitting/decoding base64 fields.
    internal static async IAsyncEnumerable<string> ReadLinesAsync(TextReader reader, int maxLineChars, Budget budget,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var line = new StringBuilder();
        var buffer = new char[4096];
        var encoder = Encoding.UTF8.GetEncoder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
        {
            budget.Consume(encoder.GetByteCount(buffer, 0, read, flush: false));
            for (var i = 0; i < read; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (buffer[i] == '\n')
                {
                    yield return line.ToString().TrimEnd('\r');
                    line.Clear();
                }
                else
                {
                    if (line.Length >= maxLineChars) throw new WslOutputLimitException();
                    line.Append(buffer[i]);
                }
            }
        }
        budget.Consume(encoder.GetByteCount([], 0, 0, flush: true));
        if (line.Length > 0) yield return line.ToString().TrimEnd('\r');
    }
}
