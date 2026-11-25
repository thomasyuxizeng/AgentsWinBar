using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexBarWin.Services.Native;

/// <summary>
/// A pooled byte buffer holding credential bytes. Disposing zeroes it before it goes back to the pool, so token
/// bytes do not linger in reusable memory.
/// </summary>
public sealed class SensitiveBuffer : IDisposable
{
    private const int MaxPooledBytes = 512 * 1024;
    private byte[]? _array;
    private readonly bool _pooled;

    public SensitiveBuffer(int capacity)
    {
        var size = Math.Max(1, capacity);
        if (size <= MaxPooledBytes)
        {
            _array = ArrayPool<byte>.Shared.Rent(size);
            _pooled = true;
        }
        else
        {
            _array = new byte[size];
            _pooled = false;
        }
    }

    public int Length { get; internal set; }
    public ReadOnlySpan<byte> Span => _array.AsSpan(0, Length);
    internal byte[] Array => _array ?? throw new ObjectDisposedException(nameof(SensitiveBuffer));

    public static SensitiveBuffer From(ReadOnlySpan<byte> bytes)
    {
        var buffer = new SensitiveBuffer(bytes.Length);
        bytes.CopyTo(buffer.Array);
        buffer.Length = bytes.Length;
        return buffer;
    }

    public void Dispose()
    {
        var array = Interlocked.Exchange(ref _array, null);
        if (array == null) return;
        CryptographicOperations.ZeroMemory(array);
        if (_pooled)
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }
}

/// <summary>Read-only access to credential files that their owning apps rewrite at any time.</summary>
public static class NativeCredentialFile
{
    /// <summary>Refuse absurd files rather than reading them whole (the Claude file is about 36 KB).</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>How long to wait before the single retry when the owner was caught mid-rewrite.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(75);

    /// <summary>
    /// Reads a whole file opened with <c>FileShare.ReadWrite | FileShare.Delete</c>. Returns null when the file does not
    /// exist. Never writes, never locks out the owner. The owning app may be rewriting the file at that moment, so
    /// content that is empty or not valid JSON is read once more after <see cref="RetryDelay"/>; whatever the second
    /// read holds is returned. <paramref name="delayBeforeRetry"/> replaces the wait (for tests).
    /// </summary>
    public static async Task<SensitiveBuffer?> ReadAsync(string path, CancellationToken ct, Func<CancellationToken, Task>? delayBeforeRetry = null)
    {
        var first = await ReadOnceAsync(path, ct).ConfigureAwait(false);
        if (first == null) return null;
        if (IsCompleteJson(first.Span)) return first;

        first.Dispose(); // zeroes the half-written content before waiting
        await (delayBeforeRetry?.Invoke(ct) ?? Task.Delay(RetryDelay, ct)).ConfigureAwait(false);
        return await ReadOnceAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>True for exactly one complete JSON document (empty content or a truncated document is not).</summary>
    private static bool IsCompleteJson(ReadOnlySpan<byte> bytes)
    {
        var content = WithoutBom(bytes);
        if (content.IsEmpty) return false;
        try
        {
            var reader = new Utf8JsonReader(content, ReaderOptions);
            while (reader.Read()) { }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<SensitiveBuffer?> ReadOnceAsync(string path, CancellationToken ct)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0
            });
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        await using (stream.ConfigureAwait(false))
            return await ReadToEndAsync(stream, stream.Length, ct).ConfigureAwait(false);
    }

    /// <summary>Reads <paramref name="stream"/> to its end into a sensitive buffer; <paramref name="length"/> is its size when opened.</summary>
    internal static async Task<SensitiveBuffer> ReadToEndAsync(Stream stream, long length, CancellationToken ct)
    {
        if (length > MaxBytes) throw new InvalidDataException("Credential file is unexpectedly large.");
        // The owner may grow the file between Length and the read; leave room, grow if needed and read to EOF.
        var buffer = new SensitiveBuffer((int)length + 4096);
        try
        {
            var total = 0;
            while (true)
            {
                if (total == buffer.Array.Length)
                {
                    // Copy into a larger buffer; disposing the old one zeroes it before it goes back to the pool.
                    var larger = new SensitiveBuffer((int)Math.Min((long)total * 2, MaxBytes + 1L));
                    buffer.Array.AsSpan(0, total).CopyTo(larger.Array);
                    buffer.Dispose();
                    buffer = larger;
                }
                // Never read more than one byte past the cap: enough to tell that the file is too large.
                var room = Math.Min(buffer.Array.Length - total, MaxBytes + 1 - total);
                var read = await stream.ReadAsync(buffer.Array.AsMemory(total, room), ct).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > MaxBytes) throw new InvalidDataException("Credential file is unexpectedly large.");
            }
            buffer.Length = total;
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>Skips a UTF-8 byte order mark if present.</summary>
    public static ReadOnlySpan<byte> WithoutBom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith("﻿"u8) ? bytes[3..] : bytes;

    public static JsonReaderOptions ReaderOptions => new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
}

/// <summary>Reads claims from a JWT payload without verifying it (scheduling hints only; the server still decides).</summary>
public static class NativeJwt
{
    /// <summary>The integer "exp" claim at the top level of the payload, or null.</summary>
    public static DateTimeOffset? ReadExpiry(string? token)
    {
        DateTimeOffset? expiry = null;
        WithPayload(token, payload =>
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isExp = reader.ValueTextEquals("exp"u8);
                reader.Read();
                if (isExp && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var seconds) &&
                    seconds is > 0 and < 253402300799)
                {
                    if (expiry != null) { expiry = null; return; } // duplicate claims are ambiguous
                    expiry = DateTimeOffset.FromUnixTimeSeconds(seconds);
                }
                else reader.Skip();
            }
        });
        return expiry;
    }

    /// <summary>ChatGPT account id from an OpenAI token: top-level or under "https://api.openai.com/auth".</summary>
    public static string? ReadChatGptAccountId(string? token)
    {
        string? found = null;
        WithPayload(token, payload =>
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;
            while (found == null && reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("chatgpt_account_id"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String) found = NonEmpty(reader.GetString());
                    else reader.Skip(); // an object or array value is stepped over whole
                    continue;
                }
                if (reader.ValueTextEquals("https://api.openai.com/auth"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var match = reader.ValueTextEquals("chatgpt_account_id"u8);
                        reader.Read();
                        if (match && reader.TokenType == JsonTokenType.String) found ??= NonEmpty(reader.GetString());
                        else reader.Skip();
                    }
                    continue;
                }
                reader.Read();
                reader.Skip();
            }
        });
        return found;
    }

    private delegate void PayloadAction(ReadOnlySpan<byte> payload);

    private static void WithPayload(string? token, PayloadAction action)
    {
        if (string.IsNullOrEmpty(token)) return;
        var first = token.IndexOf('.');
        var second = first < 0 ? -1 : token.IndexOf('.', first + 1);
        if (first <= 0 || second <= first + 1 || token.IndexOf('.', second + 1) >= 0) return;
        var segment = token.AsSpan(first + 1, second - first - 1);
        var max = Base64Url.GetMaxDecodedLength(segment.Length);
        var rented = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            // TryDecodeFromChars still throws FormatException on characters outside the alphabet.
            try
            {
                if (!Base64Url.TryDecodeFromChars(segment, rented, out var written)) return;
                action(rented.AsSpan(0, written));
            }
            catch (Exception ex) when (ex is JsonException or FormatException) { }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented);
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Small parsing helpers shared by the clients.</summary>
public static class NativeParse
{
    /// <summary>
    /// Parses ISO 8601 / RFC 3339 timestamps, including Go's nanosecond precision ("...05.123456789+11:00"), which
    /// .NET rejects beyond 7 fractional digits. Go's zero time and unparseable values return null.
    /// </summary>
    public static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            var end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
            if (end - dot - 1 > 7) text = string.Concat(text.AsSpan(0, dot + 8), text.AsSpan(end));
        }
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed)) return null;
        return parsed.Year <= 1 ? null : parsed.ToUniversalTime();
    }

    /// <summary>Lower-case alphanumeric slug with single dashes, as upstream builds window ids.</summary>
    public static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) { builder.Append(c); lastWasDash = false; }
            else if (!lastWasDash) { builder.Append('-'); lastWasDash = true; }
        }
        return builder.ToString().Trim('-');
    }

    public static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>"Resets in 2d 3h" / "Resets in 4h 5m", the shim's wording for Gemini windows.</summary>
    public static string ResetsInDescription(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var minutes = Math.Max(0, (long)Math.Floor((resetsAt - now).TotalMinutes));
        long days = minutes / 1440, hours = minutes % 1440 / 60, mins = minutes % 60;
        return "Resets in " + (days > 0 ? $"{days}d {hours}h" : $"{hours}h {mins}m");
    }

    /// <summary>Plain wording for an expired sign-in, e.g. "12 minutes", "3 hours", "2 days".</summary>
    public static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "less than a minute";
        if (span.TotalHours < 1) return Plural((int)span.TotalMinutes, "minute");
        if (span.TotalDays < 1) return Plural((int)span.TotalHours, "hour");
        return Plural((int)span.TotalDays, "day");
        static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
    }
}
