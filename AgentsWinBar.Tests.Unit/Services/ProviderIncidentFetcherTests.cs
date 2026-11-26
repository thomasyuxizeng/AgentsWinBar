using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class ProviderIncidentFetcherTests
{
    private sealed class TestClock(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class DelegatingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return send(request, ct);
        }
    }

    private static readonly DateTimeOffset InitialTime = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static string ClaudeJson => """
        {
          "page": { "url": "https://status.claude.com" },
          "status": { "indicator": "minor", "description": "Partially Degraded Service" },
          "incidents": [
            {
              "name": "Elevated errors on platform.claude.com",
              "status": "monitoring",
              "resolved_at": null,
              "impact": "major",
              "incident_updates": [
                {
                  "body": "Mitigated errors loading usage data.",
                  "display_at": "2026-10-11T11:45:00Z"
                }
              ]
            }
          ]
        }
        """;

    [TestMethod]
    public async Task GetIncidentAsync_HttpSuccess_ParsesAndCachesStatus()
    {
        var handler = new DelegatingHandler((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ClaudeJson)
        }));
        var clock = new TestClock(InitialTime);
        using var fetcher = new ProviderIncidentFetcher(handler, clock);

        var status = await fetcher.GetIncidentAsync("claude");

        status.Should().NotBeNull();
        status!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        status.IncidentTitle.Should().Be("Elevated errors on platform.claude.com");
        status.LatestUpdateText.Should().Be("Mitigated errors loading usage data.");
        handler.Requests.Should().Be(1);
    }

    [TestMethod]
    public async Task GetIncidentAsync_WithinTtl_ReturnsCachedWithoutSecondHttpRequest()
    {
        var handler = new DelegatingHandler((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ClaudeJson)
        }));
        var clock = new TestClock(InitialTime);
        using var fetcher = new ProviderIncidentFetcher(handler, clock);

        var status1 = await fetcher.GetIncidentAsync("claude");
        clock.Current = InitialTime.AddMinutes(4); // Within 5 min TTL
        var status2 = await fetcher.GetIncidentAsync("claude");

        status2.Should().BeSameAs(status1);
        handler.Requests.Should().Be(1);
    }

    [TestMethod]
    public async Task GetIncidentAsync_AfterTtl_RefetchesOverHttp()
    {
        var handler = new DelegatingHandler((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ClaudeJson)
        }));
        var clock = new TestClock(InitialTime);
        using var fetcher = new ProviderIncidentFetcher(handler, clock);

        await fetcher.GetIncidentAsync("claude");
        clock.Current = InitialTime.AddMinutes(6); // Beyond 5 min TTL
        await fetcher.GetIncidentAsync("claude");

        handler.Requests.Should().Be(2);
    }

    [TestMethod]
    public async Task GetIncidentAsync_ConcurrentCallers_ExecutesSingleHttpRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new DelegatingHandler(async (req, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaudeJson) };
        });

        var clock = new TestClock(InitialTime);
        using var fetcher = new ProviderIncidentFetcher(handler, clock);

        var tasks = Enumerable.Range(0, 15).Select(_ => fetcher.GetIncidentAsync("claude")).ToArray();
        await entered.Task;
        release.SetResult();

        var results = await Task.WhenAll(tasks);

        handler.Requests.Should().Be(1);
        results.Should().OnlyContain(s => s != null);
        results.Should().OnlyContain(s => ReferenceEquals(s, results[0]));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.BadGateway)]
    public async Task GetIncidentAsync_HttpNon200_ReturnsNullWithoutThrowing(HttpStatusCode statusCode)
    {
        var handler = new DelegatingHandler((req, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("Server Error")
        }));
        var clock = new TestClock(InitialTime);
        using var fetcher = new ProviderIncidentFetcher(handler, clock);

        var status = await fetcher.GetIncidentAsync("claude");

        status.Should().BeNull();
    }

    [TestMethod]
    public async Task GetIncidentAsync_OversizedResponse_ReturnsNullWithoutThrowing()
    {
        var hugeBody = new string(' ', ProviderIncidentFetcher.MaxFeedBytes + 100);
        var handler = new DelegatingHandler((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(hugeBody)
        }));
        var clock = new TestClock(InitialTime);
        using var fetcher = new ProviderIncidentFetcher(handler, clock);

        var status = await fetcher.GetIncidentAsync("claude");

        status.Should().BeNull();
    }

    [TestMethod]
    public async Task GetIncidentAsync_Timeout_ReturnsNullWithoutThrowing()
    {
        var handler = new DelegatingHandler(async (req, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var clock = new TestClock(InitialTime);
        using var fetcher = new ProviderIncidentFetcher(handler, clock);

        // Fetcher bounds timeout to 5 seconds internally
        var status = await fetcher.GetIncidentAsync("claude").WaitAsync(TimeSpan.FromSeconds(7));

        status.Should().BeNull();
    }

    [TestMethod]
    public async Task GetIncidentAsync_CallerCancelled_ReturnsNullImmediately()
    {
        var handler = new DelegatingHandler((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ClaudeJson)
        }));
        var clock = new TestClock(InitialTime);
        using var fetcher = new ProviderIncidentFetcher(handler, clock);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var status = await fetcher.GetIncidentAsync("claude", cts.Token);

        status.Should().BeNull();
        handler.Requests.Should().Be(0);
    }

    [TestMethod]
    public async Task GetIncidentAsync_UnknownProvider_ReturnsNullWithoutHttp()
    {
        var handler = new DelegatingHandler((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ClaudeJson)
        }));
        var clock = new TestClock(InitialTime);
        using var fetcher = new ProviderIncidentFetcher(handler, clock);

        var status = await fetcher.GetIncidentAsync("unknown_provider");

        status.Should().BeNull();
        handler.Requests.Should().Be(0);
    }

    [TestMethod]
    public async Task GetIncidentAsync_FailureCachedBrieflyFor60Seconds()
    {
        var statusCode = HttpStatusCode.InternalServerError;
        var handler = new DelegatingHandler((req, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(statusCode == HttpStatusCode.OK ? ClaudeJson : "Server Error")
        }));
        var clock = new TestClock(InitialTime);
        using var fetcher = new ProviderIncidentFetcher(handler, clock);

        // Call 1: HTTP 500 failure
        var status1 = await fetcher.GetIncidentAsync("claude");
        status1.Should().BeNull();
        handler.Requests.Should().Be(1);

        // Call 2 (30s later): should be cached failure (negative cache hit, no new HTTP request)
        clock.Current += TimeSpan.FromSeconds(30);
        statusCode = HttpStatusCode.OK; // Even if server recovered, within 60s it should not be hit
        var status2 = await fetcher.GetIncidentAsync("claude");
        status2.Should().BeNull();
        handler.Requests.Should().Be(1);

        // Call 3 (61s after call 1): negative cache expired, should make HTTP request
        clock.Current += TimeSpan.FromSeconds(31); // Total 61s elapsed
        var status3 = await fetcher.GetIncidentAsync("claude");
        status3.Should().NotBeNull();
        handler.Requests.Should().Be(2);
    }

    private sealed class ThrowingStreamContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Body should not be read on 304 Not Modified");
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            throw new InvalidOperationException("Body stream should not be opened on 304 Not Modified");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= data.Length) return ValueTask.FromResult(0);
            var toCopy = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - _pos);
            data.AsSpan(_pos, toCopy).CopyTo(buffer.Span);
            _pos += toCopy;
            return ValueTask.FromResult(toCopy);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [TestMethod]
    public void CreateDefaultHandler_Configuration_UsesDecompressionNoCookiesAnd90sIdleTimeout()
    {
        using var handler = ProviderIncidentFetcher.CreateDefaultHandler();

        handler.AutomaticDecompression.Should().Be(
            DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli);
        handler.UseCookies.Should().BeFalse();
        handler.PooledConnectionIdleTimeout.Should().Be(TimeSpan.FromSeconds(90));
    }

    [TestMethod]
    public async Task GetIncidentAsync_ConditionalGet304NotModified_RefreshesCacheWithoutBodyRead()
    {
        var lastModified = new DateTimeOffset(2026, 10, 11, 11, 55, 0, TimeSpan.Zero);
        EntityTagHeaderValue? seenIfNoneMatch = null;
        DateTimeOffset? seenIfModifiedSince = null;
        var callCount = 0;

        var handler = new DelegatingHandler((req, _) =>
        {
            callCount++;
            if (callCount == 1)
            {
                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClaudeJson) };
                ok.Headers.ETag = new EntityTagHeaderValue("\"etag-v1\"");
                ok.Content.Headers.LastModified = lastModified;
                return Task.FromResult(ok);
            }

            seenIfNoneMatch = req.Headers.IfNoneMatch.FirstOrDefault();
            seenIfModifiedSince = req.Headers.IfModifiedSince;
            var notModified = new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                Content = new ThrowingStreamContent()
            };
            notModified.Headers.ETag = new EntityTagHeaderValue("\"etag-v2\"");
            return Task.FromResult(notModified);
        });

        var clock = new TestClock(InitialTime);
        using var fetcher = new ProviderIncidentFetcher(handler, clock);

        var status1 = await fetcher.GetIncidentAsync("claude");
        status1.Should().NotBeNull();

        clock.Current = InitialTime.AddMinutes(6);
        var status2 = await fetcher.GetIncidentAsync("claude");

        seenIfNoneMatch.Should().Be(new EntityTagHeaderValue("\"etag-v1\""));
        seenIfModifiedSince.Should().Be(lastModified);
        status2.Should().BeSameAs(status1);
        handler.Requests.Should().Be(2);

        // Cache TTL should be refreshed by 304, so another call 4 minutes later uses cache without HTTP
        clock.Current = InitialTime.AddMinutes(10);
        var status3 = await fetcher.GetIncidentAsync("claude");
        status3.Should().BeSameAs(status1);
        handler.Requests.Should().Be(2);
    }

    [TestMethod]
    public async Task GetIncidentAsync_MultiChunkLargeUtf8BodyWithBom_GrowsArrayPoolAndParses()
    {
        var padded = new string(' ', 70_000);
        var json = ClaudeJson + padded;
        var payload = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(json)).ToArray();

        var handler = new DelegatingHandler((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(payload, 4096))
        }));

        var clock = new TestClock(InitialTime);
        using var fetcher = new ProviderIncidentFetcher(handler, clock);

        var status = await fetcher.GetIncidentAsync("claude");

        status.Should().NotBeNull();
        status!.IncidentTitle.Should().Be("Elevated errors on platform.claude.com");
    }

    [TestMethod]
    public async Task GetIncidentAsync_LargeBodyOver512KbPoolCap_StillParsesCleanly()
    {
        var padded = new string(' ', 600_000);
        var payload = Encoding.UTF8.GetBytes(ClaudeJson + padded);

        var handler = new DelegatingHandler((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkedStream(payload, 16_384))
        }));

        var clock = new TestClock(InitialTime);
        using var fetcher = new ProviderIncidentFetcher(handler, clock);

        var status = await fetcher.GetIncidentAsync("claude");

        status.Should().NotBeNull();
        status!.IncidentTitle.Should().Be("Elevated errors on platform.claude.com");
    }
}
