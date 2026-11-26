using System.Text;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services;

internal sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(Path.Combine(directory, "app.log"), categoryName);

    public void Dispose() { }

    private sealed class FileLogger(string path, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Warning and <= LogLevel.Critical;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var exceptionType = exception == null ? string.Empty : $" ({exception.GetType().Name})";
            RollingLogFile.Append(path, $"{DateTimeOffset.UtcNow:O} [{logLevel}] {category}: {formatter(state, null)}{exceptionType}");
        }
    }
}

internal static class RollingLogFile
{
    internal const int MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();
    private static readonly byte[] NewLine = Encoding.UTF8.GetBytes(Environment.NewLine);

    public static void Append(string path, string line)
    {
        var maxTextBytes = MaxBytes - NewLine.Length;
        var textBytes = Encoding.UTF8.GetByteCount(line);
        var bytes = new byte[Math.Min(textBytes, maxTextBytes) + NewLine.Length];
        var written = textBytes <= maxTextBytes
            ? Encoding.UTF8.GetBytes(line.AsSpan(), bytes.AsSpan())
            : EncodeTruncated(line, bytes.AsSpan(0, maxTextBytes));
        NewLine.CopyTo(bytes.AsSpan(written));
        var entry = bytes.AsSpan(0, written + NewLine.Length);

        lock (Gate)
        {
            try
            {
                var file = new FileInfo(path);
                file.Directory!.Create();
                if (file.Exists && file.Length > MaxBytes - entry.Length)
                {
                    // A crash log left by an older build may already be larger than the cap.
                    if (file.Length > MaxBytes)
                    {
                        using var oversized = file.OpenWrite();
                        oversized.SetLength(MaxBytes);
                    }
                    file.MoveTo(path + ".1", overwrite: true);
                }
                using var output = File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                output.Write(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A diagnostic write failure must not interrupt the caller.
            }
        }
    }

    private static int EncodeTruncated(string line, Span<byte> destination)
    {
        Encoding.UTF8.GetEncoder().Convert(line, destination, flush: true, out _, out var written, out _);
        return written;
    }
}
