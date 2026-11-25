using System.Net;
using System.Text;
using CodexBarWin.Services.Native;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Tests.Unit.Services.Native;

/// <summary>Fakes shared by the native usage tests. Every token here is synthetic; no test touches a real sign-in or host.</summary>
internal static class NativeTestSupport
{
    public static readonly DateTimeOffset Now = new(2026, 11, 25, 6, 0, 0, TimeSpan.Zero);

    /// <summary>A syntactically valid, unsigned, synthetic JWT with the given payload JSON.</summary>
    public static string Jwt(string payloadJson) =>
        "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic-signature";

    public static string TempFile(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexbarwin-native-tests", Guid.NewGuid().ToString("N"));
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
