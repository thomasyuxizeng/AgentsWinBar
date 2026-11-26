using System.Net;
using System.Text;
using AgentsWinBar.Services.Native;
using Microsoft.Extensions.Logging;

namespace AgentsWinBar.Tests.Unit.Services.Native;

/// <summary>Fakes shared by the native usage tests. Every token here is synthetic; no test touches a real sign-in or host.</summary>
internal static class NativeTestSupport
{
    public static readonly DateTimeOffset Now = new(2026, 11, 25, 6, 0, 0, TimeSpan.Zero);

    /// <summary>A syntactically valid, unsigned, synthetic JWT with the given payload JSON.</summary>
    public static string Jwt(string payloadJson) =>
        "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic-signature";

    public static string TempFile(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentswinbar-native-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "credentials.json");
        File.WriteAllText(path, content);
        return path;
    }

    public static void DeleteTemp(string path)
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A 200 answer whose body streams from <paramref name="body"/> (no Content-Length).</summary>
    public static HttpResponseMessage Streamed(Stream body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return response;
    }

    /// <summary>Eight MiB of padding: well past the clients' response cap.</summary>
    public const long HugePadding = 8L * 1024 * 1024;
}

/// <summary>
/// A JSON body of <c>prefix</c>, then <c>paddingBytes</c> of 'x', then <c>suffix</c>, generated on the fly.
/// <see cref="Served"/> counts the bytes handed out, so a test can tell how much of the body a reader took.
/// </summary>
internal sealed class PaddedJsonStream(string prefix, long paddingBytes, string suffix) : Stream
{
    private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
    private readonly byte[] _suffix = Encoding.UTF8.GetBytes(suffix);
    private long _served;

    public long Served => Interlocked.Read(ref _served);
    private long Total => _prefix.Length + paddingBytes + _suffix.Length;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => Served; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var written = 0;
        while (written < buffer.Length && _served < Total)
        {
            var position = _served;
            int take;
            if (position < _prefix.Length)
            {
                take = (int)Math.Min(buffer.Length - written, _prefix.Length - position);
                _prefix.AsSpan((int)position, take).CopyTo(buffer[written..]);
            }
            else if (position < _prefix.Length + paddingBytes)
            {
                take = (int)Math.Min(buffer.Length - written, _prefix.Length + paddingBytes - position);
                buffer.Slice(written, take).Fill((byte)'x');
            }
            else
            {
                var start = (int)(position - _prefix.Length - paddingBytes);
                take = Math.Min(buffer.Length - written, _suffix.Length - start);
                _suffix.AsSpan(start, take).CopyTo(buffer[written..]);
            }
            written += take;
            Interlocked.Add(ref _served, take);
        }
        return written;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Records requests (without reading header values back out) and answers from a callback.</summary>
internal sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
        return respond(request);
    }

    /// <summary>Client wired like production: allow-list handler in front of this fake.</summary>
    public HttpClient Client() => new(new NativeHttp.HostAllowListHandler(this)) { Timeout = Timeout.InfiniteTimeSpan };
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}

internal sealed class FakeCredentialSource(string? json) : IAntigravityCredentialSource
{
    public int Reads { get; private set; }
    public SensitiveBuffer? ReadBlob()
    {
        Reads++;
        return json == null ? null : SensitiveBuffer.From(Encoding.UTF8.GetBytes(json));
    }
}
