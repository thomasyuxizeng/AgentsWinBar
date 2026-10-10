using System.Net;
using System.Text;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class GeminiStatusRobustnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Current;
    }
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Requests;
        public bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return send(ct);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private static HttpResponseMessage Response(string json = "[]", HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json) };

    [TestMethod]
    [DataRow("\"modified\":\"2026-10-03T12:00:00Z\",\"end\":null", ProviderStatusIndicator.None)]
    [DataRow("\"modified\":\"2026-10-03T12:00:00Z\",\"end\":\"\"", ProviderStatusIndicator.None)]
    [DataRow("\"modified\":\"bad\",\"begin\":\"2026-10-03T12:00:00Z\"", ProviderStatusIndicator.None)]
    [DataRow("\"modified\":\"2026-10-04T12:00:00Z\"", ProviderStatusIndicator.Minor)]
    [DataRow("\"modified\":\"2026-10-10T12:00:00Z\",\"begin\":\"2020-01-01T00:00:00Z\"", ProviderStatusIndicator.Minor)]
    [DataRow("\"end\":\"2026-10-11T21:00:00+09:00\"", ProviderStatusIndicator.None)]
    [DataRow("\"end\":\"2026-10-11T08:00:01-04:00\"", ProviderStatusIndicator.Minor)]
    [DataRow("\"end\":\"2026-10-11T12:00:00\"", ProviderStatusIndicator.None)]
    [DataRow("\"modified\":\"2026-10-03T12:00:00Z\",\"end\":\"2026-10-12T12:00:00Z\"", ProviderStatusIndicator.Minor)]
    [DataRow("\"modified\":null,\"begin\":{},\"end\":42", ProviderStatusIndicator.Minor)]
    public void ParseIncidents_OldOpenAndOffsetDates_AppliesUtcAndSevenDayPolicy(string fields, ProviderStatusIndicator expected)
    {
        var json = "[{\"service_key\":\"" + GeminiStatusFallback.GeminiProductId + "\"," + fields + "}]";
        GeminiStatusFallback.ParseIncidents(json, Now)!.Indicator.Should().Be(expected);
    }

    [TestMethod]
    public void ParseIncidents_MissingOrOddFields_SkipsMalformedSiblingsAndKeepsValidIncident()
    {
        var json = """
            [null,42,"odd",{}, {"service_key":{},"affected_products":[null,42,{"id":{}}]},
             {"service_key":123}, {"affected_products":"not array"},
             {"service_key":"npdyhgECDJ6tB66MxXyo","status_impact":{},"severity":[],
              "external_desc":null,"most_recent_update":{"text":{}},"modified":"bad"}]
            """;
        var status = GeminiStatusFallback.ParseIncidents(json, Now);
        status!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        status.Description.Should().Be("Minor service disruption");
        status.UpdatedAt.Should().Be(Now.UtcDateTime);
    }

    [TestMethod]
    public void ParseIncidents_UnknownImpactAndTimezone_PreservesMinorAndUtcTimestamp()
    {
        var status = GeminiStatusFallback.ParseIncidents("""
            [{"service_key":"npdyhgECDJ6tB66MxXyo","status_impact":"SOMETHING_NEW",
              "modified":"2026-10-11T20:30:00+09:00"}]
            """, Now);
        status!.Indicator.Should().Be(ProviderStatusIndicator.Minor);
        status.UpdatedAt.Should().Be(Now.UtcDateTime.AddMinutes(-30));
        status.UpdatedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetStatusAsync_ConcurrentCallers_UsesOneRequestIncludingFailures(bool failure)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct);
            return Response(code: failure ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK); });
        using var client = new HttpClient(handler);
        var clock = new Clock();
        using var fallback = new GeminiStatusFallback(client, clock);
        var calls = Enumerable.Range(0, 20).Select(_ => fallback.GetStatusAsync()).ToArray();
        await entered.Task; release.SetResult();
        var statuses = await Task.WhenAll(calls);
        handler.Requests.Should().Be(1);
        if (failure) statuses.Should().OnlyContain(s => s == null);
        else statuses.Should().OnlyContain(s => ReferenceEquals(s, statuses[0]));
        clock.Current = Now.AddMinutes(5);
        await fallback.GetStatusAsync();
        handler.Requests.Should().Be(2);
    }

    [TestMethod]
    public async Task GetStatusAsync_CancelledWaiter_DoesNotCancelOtherCallerOrCacheFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); return Response(); });
        using var client = new HttpClient(handler);
        using var fallback = new GeminiStatusFallback(client, new Clock());
        var owner = fallback.GetStatusAsync(); await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var waiter = fallback.GetStatusAsync(cancellation.Token); cancellation.Cancel();
        (await waiter).Should().BeNull();
        release.SetResult();
        var status = await owner;
        (await fallback.GetStatusAsync()).Should().BeSameAs(status);
        handler.Requests.Should().Be(1);
        (await fallback.GetStatusAsync(cancellation.Token)).Should().BeNull();
    }

    [TestMethod]
    public async Task GetStatusAsync_CancelledFetch_ReturnsNullAndAllowsRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Response(); });
        using var client = new HttpClient(handler);
        using var fallback = new GeminiStatusFallback(client, new Clock());
        using var cancellation = new CancellationTokenSource();
        var pending = fallback.GetStatusAsync(cancellation.Token); await entered.Task; cancellation.Cancel();
        (await pending).Should().BeNull();
        using var secondCancellation = new CancellationTokenSource();
        var retry = fallback.GetStatusAsync(secondCancellation.Token); secondCancellation.Cancel();
        (await retry).Should().BeNull();
        handler.Requests.Should().Be(2);
    }

    [TestMethod]
    public async Task Dispose_DuringFetchAndQueuedWaiter_CancelsSafelyAndLeavesBorrowedClientAlive()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Response(); });
        using var client = new HttpClient(handler);
        var fallback = new GeminiStatusFallback(client, new Clock());
        var owner = fallback.GetStatusAsync(); await entered.Task;
        var waiter = fallback.GetStatusAsync();
        fallback.Dispose(); fallback.Dispose();
        (await owner).Should().BeNull(); (await waiter).Should().BeNull();
        (await fallback.GetStatusAsync()).Should().BeNull();
        handler.Disposed.Should().BeFalse();
    }

    [TestMethod]
    public async Task Dispose_OwnedHandler_DisposesItAndReturnsNullEvenAfterCachedSuccess()
    {
        var handler = new Handler(_ => Task.FromResult(Response()));
        var fallback = new GeminiStatusFallback(handler, new Clock());
        (await fallback.GetStatusAsync()).Should().NotBeNull();
        fallback.Dispose();
        handler.Disposed.Should().BeTrue();
        (await fallback.GetStatusAsync()).Should().BeNull();
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await base.ReadAsync(buffer, ct); BytesRead += read; return read;
        }
    }
    private sealed class UnknownLengthContent(Stream stream) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }

    [TestMethod]
    [DataRow(344 * 1024, false, true)]
    [DataRow(GeminiStatusFallback.MaxFeedBytes, false, true)]
    [DataRow(GeminiStatusFallback.MaxFeedBytes + 100, false, false)]
    [DataRow(GeminiStatusFallback.MaxFeedBytes + 100, true, false)]
    public async Task GetStatusAsync_LargeFeed_BoundsReadEvenWithoutContentLength(int bytes, bool declared, bool accepted)
    {
        var json = "[" + new string(' ', bytes - 2) + "]";
        var stream = new CountingStream(Encoding.UTF8.GetBytes(json));
        var content = new UnknownLengthContent(stream);
        if (declared) content.Headers.ContentLength = bytes;
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var client = new HttpClient(handler);
        using var fallback = new GeminiStatusFallback(client, new Clock());
        var status = await fallback.GetStatusAsync();
        (status != null).Should().Be(accepted);
        stream.BytesRead.Should().BeLessThanOrEqualTo(declared ? 0 : GeminiStatusFallback.MaxFeedBytes + 1);
    }

    private sealed class SlowStream(TaskCompletionSource entered) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return 0; }
    }

    [TestMethod]
    public async Task GetStatusAsync_Utf8Bom_AcceptsFeed()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent([0xEF, 0xBB, 0xBF, (byte)'[', (byte)']']) }));
        using var client = new HttpClient(handler);
        using var fallback = new GeminiStatusFallback(client, new Clock());
        (await fallback.GetStatusAsync())!.Indicator.Should().Be(ProviderStatusIndicator.None);
    }

    [TestMethod]
    public async Task GetStatusAsync_ClockMovesBackwards_RefetchesRatherThanExtendingTtl()
    {
        using var handler = new Handler(_ => Task.FromResult(Response()));
        using var client = new HttpClient(handler);
        var clock = new Clock();
        using var fallback = new GeminiStatusFallback(client, clock);
        await fallback.GetStatusAsync(); clock.Current = Now.AddMinutes(-1);
        await fallback.GetStatusAsync();
        handler.Requests.Should().Be(2);
    }

    [TestMethod]
    public async Task GetStatusAsync_CancellationDuringBodyRead_ReturnsNullPromptly()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new UnknownLengthContent(new SlowStream(entered)) }));
        using var client = new HttpClient(handler);
        using var fallback = new GeminiStatusFallback(client, new Clock());
        using var cancellation = new CancellationTokenSource();
        var pending = fallback.GetStatusAsync(cancellation.Token); await entered.Task; cancellation.Cancel();
        (await pending.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeNull();
    }

    [TestMethod]
    public async Task GetStatusAsync_Timeout_ReturnsNullAndReleasesGate()
    {
        var requests = 0;
        using var handler = new Handler(async ct =>
        {
            if (Interlocked.Increment(ref requests) == 1) await Task.Delay(Timeout.Infinite, ct);
            return Response();
        });
        using var client = new HttpClient(handler);
        var clock = new Clock();
        using var fallback = new GeminiStatusFallback(client, clock);
        (await fallback.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(7))).Should().BeNull();
        clock.Current += GeminiStatusFallback.CacheTtl;
        (await fallback.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(1))).Should().NotBeNull();
        handler.Requests.Should().Be(2);
    }
}
