using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Services;

/// <summary>
/// Writes Warning and above to app.log. The formatted message is never written: only the message template, with each
/// argument kept when it is safe to keep on disk (see <see cref="SafeLogMessage"/>). Exceptions add their type only.
/// </summary>
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
            var message = SafeLogMessage.Render(state) is { } text ? ": " + text : string.Empty;
            RollingLogFile.Append(path, $"{DateTimeOffset.UtcNow:O} [{logLevel}] {category}{message}{exceptionType}");
        }
    }
}

/// <summary>
/// Renders a log message for disk without free text. Arguments can carry file paths, process output or response text
/// from the user's machine, so only values that cannot hold such text are written; anything else becomes
/// <see cref="Omitted"/>.
/// </summary>
internal static class SafeLogMessage
{
    internal const string Omitted = "[omitted]";
    internal const int MaxTextLength = 40;
    private const string OriginalFormatKey = "{OriginalFormat}";

    /// <summary>
    /// The message template with each placeholder replaced by its argument when that argument is safe, else
    /// <see cref="Omitted"/>. Null when the state carries no template.
    /// </summary>
    public static string? Render<TState>(TState state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> values) return null;
        var template = FindTemplate(values);
        if (template == null) return null;

        var text = new StringBuilder(template.Length);
        var hole = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{') { text.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}') { text.Append('}'); i++; continue; }
            var end = c == '{' ? template.IndexOf('}', i + 1) : -1;
            if (end < 0) { text.Append(c); continue; }
            // A placeholder is "{Name}", "{Name,alignment}" or "{Name:format}"; arguments follow placeholders in order.
            var spec = template.AsSpan(i + 1, end - i - 1);
            var formatStart = spec.IndexOf(':');
            var format = formatStart < 0 ? null : spec[(formatStart + 1)..].ToString();
            text.Append(TryGetArgument(values, hole++, out var value) && TryFormat(value, format, out var formatted)
                ? formatted : Omitted);
            i = end;
        }
        return text.ToString();
    }

    /// <summary>Whether <paramref name="value"/> is a number, bool, enum, time, Guid or short identifier-like string.</summary>
    internal static bool TryFormat(object? value, string? format, out string text)
    {
        text = string.Empty;
        try
        {
            switch (value)
            {
                case string s when IsSafeText(s):
                    text = s;
                    return true;
                case bool b:
                    text = b ? bool.TrueString : bool.FalseString;
                    return true;
                case Enum or TimeSpan or DateTime or DateTimeOffset or Guid
                    or byte or sbyte or short or ushort or int or uint or long or ulong or nint or nuint
                    or float or double or decimal or Half or Int128 or UInt128:
                    text = ((IFormattable)value).ToString(format, CultureInfo.InvariantCulture);
                    return true;
                default:
                    return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>At most 40 characters, only ASCII letters, digits and <c>_ . : -</c> (provider names, status words).</summary>
    internal static bool IsSafeText(string text) =>
        text.Length <= MaxTextLength && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-');

    private static string? FindTemplate(IReadOnlyList<KeyValuePair<string, object?>> values)
    {
        try
        {
            // Microsoft.Extensions.Logging puts the template last; search backward from there.
            for (var i = values.Count - 1; i >= 0; i--)
            {
                if (values[i].Key == OriginalFormatKey) return values[i].Value as string;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or FormatException)
        {
            // A state whose arguments do not match its template.
        }
        return null;
    }

    private static bool TryGetArgument(IReadOnlyList<KeyValuePair<string, object?>> values, int index, out object? value)
    {
        value = null;
        try
        {
            if (index >= values.Count) return false;
            var pair = values[index];
            if (pair.Key == OriginalFormatKey) return false;
            value = pair.Value;
            return true;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or FormatException)
        {
            // More placeholders than arguments.
            return false;
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

    /// <summary>
    /// The exception's type and stack frames, then those of up to three inner exceptions, outermost first. Messages are
    /// left out: they can carry file paths, response text or other data from the user's machine.
    /// </summary>
    internal static string DescribeException(Exception exception)
    {
        var text = new StringBuilder();
        var depth = 0;
        for (var current = exception; current != null && depth < 4; current = current.InnerException, depth++)
        {
            text.Append(depth == 0 ? " (" : Environment.NewLine + "  inner (").Append(current.GetType().FullName).Append(')');
            if (current.StackTrace is { Length: > 0 } stack) text.Append(Environment.NewLine).Append(stack);
        }
        return text.ToString();
    }

    private static int EncodeTruncated(string line, Span<byte> destination)
    {
        Encoding.UTF8.GetEncoder().Convert(line, destination, flush: true, out _, out var written, out _);
        return written;
    }
}
