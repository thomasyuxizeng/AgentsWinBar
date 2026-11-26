using System.Net;
using AwesomeAssertions;
using AgentsWinBar.Models;
using AgentsWinBar.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgentsWinBar.Tests.Unit.Services;

[TestClass]
public class AdminKeyServiceTests
{
    // A synthetic, test-only value built at run time, never a credential. Its last 4 characters differ from the rest.
    private static readonly string Body = string.Concat(Enumerable.Repeat("fakebody", 10));
    private const string Tail = "Wq9Z";
    private static readonly string Key = "sk-ant-admin01-" + Body + Tail;
    private const string Name = ApiBillingService.AdminKeyEnvironmentVariable;

    private sealed class Handler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Keys { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Keys.Add(request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : "");
            return Respond(request, ct);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((level, formatter(state, exception), exception));
    }

    private sealed record Write(string Name, string? Value, EnvironmentVariableTarget Target, int Thread, bool PoolThread,
        SynchronizationContext? Context);

    private sealed class MarkerContext : SynchronizationContext { }

    private readonly Handler _handler = new();
    private readonly CapturingLogger<AdminKeyService> _logger = new();
    private readonly List<EnvironmentVariableTarget> _reads = [];
    private readonly List<Write> _writes = [];
    private string? _processKey;
    private string? _userKey;
    private Action<string?, EnvironmentVariableTarget>? _beforeWrite;
    private int _keyChanged;
    private AdminKeyService _service = null!;

    [TestInitialize]
    public void Initialize()
    {
        _service = new AdminKeyService(_logger, (name, target) =>
        {
            name.Should().Be(Name);
            _reads.Add(target);
            return target == EnvironmentVariableTarget.Process ? _processKey : _userKey;
        }, (name, value, target) =>
        {
            _beforeWrite?.Invoke(value, target);
            _writes.Add(new(name, value, target, Environment.CurrentManagedThreadId,
                Thread.CurrentThread.IsThreadPoolThread, SynchronizationContext.Current));
            if (target == EnvironmentVariableTarget.Process) _processKey = value;
            else _userKey = value;
        }, _handler);
        _service.KeyChanged += (_, _) => Interlocked.Increment(ref _keyChanged);
    }

    private void Status(HttpStatusCode status) => _handler.Respond = (_, _) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("untrusted body " + Key) });

    /// <summary>No log line carries the key or any recognizable part of it, and no exception object is logged.</summary>
    private void LogsShouldNotLeakKey() => _logger.Entries.Should().OnlyContain(e => e.Exception == null &&
        !e.Text.Contains(Key) && !e.Text.Contains("sk-ant") && !e.Text.Contains("fakebody") && !e.Text.Contains(Tail));

    // Format checks

    [TestMethod]
    [DataRow("null")]
    [DataRow("empty")]
    [DataRow("whitespace")]
    [DataRow("other prefix")]
    [DataRow("prefix case")]
    [DataRow("too short")]
    [DataRow("too long")]
    [DataRow("inner space")]
    [DataRow("inner tab")]
    [DataRow("non-ASCII")]
    [DataRow("control character")]
    public async Task TestAsync_MalformedKey_ReturnsBadFormatWithoutRequest(string kind)
    {
        var candidate = kind switch
        {
            "null" => null,
            "empty" => "",
            "whitespace" => "  \r\n ",
            "other prefix" => "sk-proj-" + Body + Tail,
            "prefix case" => "SK-ANT-ADMIN01-" + Body + Tail,
            "too short" => "sk-ant-admin01-abc",
            "too long" => "sk-ant-admin01-" + new string('a', AdminKeyService.MaxKeyLength),
            "inner space" => "sk-ant-admin01-" + Body + " " + Tail,
            "inner tab" => "sk-ant-admin01-" + Body + "\t" + Tail,
            "non-ASCII" => "sk-ant-admin01-" + Body + "é" + Tail,
            "control character" => "sk-ant-admin01-" + Body + "\u0001" + Tail,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        (await _service.TestAsync(candidate)).Should().Be(AdminKeyCheck.BadFormat);
        _handler.Requests.Should().BeEmpty();
        _writes.Should().BeEmpty();
    }

    [TestMethod]
    public void Normalize_PaddedWellFormedKey_ReturnsTrimmedKey()
    {
        AdminKeyService.Normalize("  " + Key + "\r\n").Should().Be(Key);
        AdminKeyService.Normalize("sk-ant-admin" + new string('a', AdminKeyService.MinKeyLength - 12)).Should().NotBeNull();
        AdminKeyService.Normalize("sk-ant-admin" + new string('a', AdminKeyService.MinKeyLength - 13)).Should().BeNull();
        AdminKeyService.Normalize("sk-ant-admin" + new string('a', AdminKeyService.MaxKeyLength - 12)).Should().NotBeNull();
    }

    [TestMethod]
    [DataRow("sk-ant-admin01-")]
    [DataRow("sk-ant-usr-")]
    [DataRow("sk-ant-api03-")]
    public void Normalize_AnyAnthropicKeyForm_LeavesTheSubtypeToTheRequest(string prefix) =>
        AdminKeyService.Normalize(prefix + Body + Tail).Should().Be(prefix + Body + Tail);

    // HTTP outcomes

    [TestMethod]
    public async Task TestAsync_PaddedKey_SendsOneTrimmedAdminApiRequest()
    {
        (await _service.TestAsync("  " + Key + "\n")).Should().Be(AdminKeyCheck.Valid);

        var request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.AbsoluteUri.Should().Be("https://api.anthropic.com/v1/organizations/api_keys?limit=1");
        _handler.Keys.Single().Should().Be(Key);
        request.Headers.GetValues("anthropic-version").Single().Should().Be("2023-06-01");
        request.Headers.UserAgent.ToString().Should().StartWith("AgentsWinBar/");
        _writes.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(200)]
    [DataRow(204)]
    public async Task TestAsync_SuccessStatus_ReturnsValid(int status)
    {
        Status((HttpStatusCode)status);
        (await _service.TestAsync(Key)).Should().Be(AdminKeyCheck.Valid);
    }

    [TestMethod]
    [DataRow(401)]
    [DataRow(403)]
    public async Task TestAsync_UnauthorizedOrForbidden_ReturnsRejected(int status)
    {
        Status((HttpStatusCode)status);
        (await _service.TestAsync(Key)).Should().Be(AdminKeyCheck.Rejected);
        _logger.Entries.Should().Contain(e => e.Text.Contains(status.ToString()));
        LogsShouldNotLeakKey();
    }

    [TestMethod]
    [DataRow(429)]
    [DataRow(500)]
    [DataRow(502)]
    [DataRow(503)]
    public async Task TestAsync_RateLimitOrServerError_ReturnsUnreachable(int status)
    {
        Status((HttpStatusCode)status);
        (await _service.TestAsync(Key)).Should().Be(AdminKeyCheck.Unreachable);
        LogsShouldNotLeakKey();
    }

    [TestMethod]
    [DataRow(302)]
    [DataRow(400)]
    [DataRow(404)]
    public async Task TestAsync_UnlistedStatus_ReturnsUnreachable(int status)
    {
        // A redirect is never followed and a status that says nothing about the key cannot confirm or refuse it.
        Status((HttpStatusCode)status);
        (await _service.TestAsync(Key)).Should().Be(AdminKeyCheck.Unreachable);
        _handler.Requests.Should().ContainSingle();
    }

    [TestMethod]
    [DataRow("network")]
    [DataRow("timeout")]
    [DataRow("unexpected")]
    public async Task TestAsync_NetworkErrorOrTimeout_ReturnsUnreachableAndLogsTypeOnly(string failure)
    {
        _handler.Respond = (_, _) =>
        {
            if (failure == "network") throw new HttpRequestException("connection failed for " + Key);
            if (failure == "timeout") throw new TaskCanceledException("timed out " + Key, new TimeoutException(Key));
            throw new InvalidOperationException(Key);
        };

        (await _service.TestAsync(Key)).Should().Be(AdminKeyCheck.Unreachable);
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
        LogsShouldNotLeakKey();
    }

    [TestMethod]
    public async Task TestAsync_CallerCancels_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        _handler.Respond = async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        var test = () => _service.TestAsync(Key, cts.Token);
        await test.Should().ThrowAsync<OperationCanceledException>();
        LogsShouldNotLeakKey();
    }

    [TestMethod]
    public async Task TestSavedAsync_KeyOnlyInUserEnvironment_TestsUserKey()
    {
        _processKey = " "; _userKey = Key;
        (await _service.TestSavedAsync()).Should().Be(AdminKeyCheck.Valid);
        _handler.Keys.Should().Equal(Key);
        _reads.Should().Equal(EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User);
    }

    [TestMethod]
    public async Task TestSavedAsync_ProcessAndUserKeys_TestsProcessKey()
    {
        var other = "sk-ant-admin01-" + new string('b', 40) + "Pq3X";
        _processKey = Key; _userKey = other;
        Status(HttpStatusCode.Forbidden);
        (await _service.TestSavedAsync()).Should().Be(AdminKeyCheck.Rejected);
        _handler.Keys.Should().Equal(Key);
        _reads.Should().Equal(EnvironmentVariableTarget.Process);
    }

    [TestMethod]
    public async Task TestSavedAsync_NoKeySet_ReturnsBadFormatWithoutRequest()
    {
        (await _service.TestSavedAsync()).Should().Be(AdminKeyCheck.BadFormat);
        _handler.Requests.Should().BeEmpty();
    }

    // Status

    [TestMethod]
    public void GetStatus_KeyInUserEnvironment_ShowsOnlyLastFour()
    {
        _userKey = Key;
        var status = _service.GetStatus();
        status.IsSet.Should().BeTrue();
        status.LastFour.Should().Be(Tail);
        status.ToString().Should().NotContain("sk-ant").And.NotContain("fakebody");
        _reads.Should().Equal(EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User);
    }

    [TestMethod]
    public void GetStatus_ProcessKeySet_ReadsProcessFirst()
    {
        _processKey = "  " + Key + " "; _userKey = "sk-ant-admin01-" + new string('b', 40) + "Pq3X";
        _service.GetStatus().Should().Be(new AdminKeyStatus(true, Tail));
        _reads.Should().Equal(EnvironmentVariableTarget.Process);
    }

    [TestMethod]
    public void GetStatus_NoKey_ReturnsNotSet()
    {
        _processKey = ""; _userKey = "   ";
        _service.GetStatus().Should().Be(new AdminKeyStatus(false, null));
    }

    [TestMethod]
    [DataRow("short1234")]
    [DataRow("sk-ant-admin01-abcdefghijklmnopqrstu!@#$")]
    [DataRow("sk-ant-admin01-abcdefghijklmnopqrstuvwxé")]
    public void GetStatus_ShortOrUnusualValue_HidesLastFour(string value)
    {
        _userKey = value;
        _service.GetStatus().Should().Be(new AdminKeyStatus(true, null));
    }

    [TestMethod]
    public void GetStatus_ReaderThrows_ReportsNotSetAndLogsTypeOnly()
    {
        var service = new AdminKeyService(_logger, (_, _) => throw new InvalidOperationException(Key), handler: _handler);
        service.GetStatus().Should().Be(new AdminKeyStatus(false, null));
        _logger.Entries.Should().ContainSingle(e => e.Text.Contains(nameof(InvalidOperationException)));
        LogsShouldNotLeakKey();
    }

    // Save and remove

    [TestMethod]
    public async Task SaveAsync_WellFormedKey_WritesTrimmedKeyToUserThenProcess()
    {
        (await _service.SaveAsync("\t" + Key + "  ")).Should().BeTrue();

        _writes.Select(w => (w.Name, w.Value, w.Target)).Should().Equal(
            (Name, Key, EnvironmentVariableTarget.User), (Name, Key, EnvironmentVariableTarget.Process));
        _keyChanged.Should().Be(1);
        _handler.Requests.Should().BeEmpty();
        _service.GetStatus().Should().Be(new AdminKeyStatus(true, Tail));
        LogsShouldNotLeakKey();
    }

    [TestMethod]
    public async Task SaveAsync_CalledFromUiContext_WritesOnThreadPoolWithoutBlockingCaller()
    {
        (await RunFromUiContextAsync(() => _service.SaveAsync(Key))).Should().BeTrue();
        _writes.Select(w => w.Value).Should().Equal(Key, Key);
    }

    /// <summary>
    /// Starts <paramref name="operation"/> under a UI-like synchronization context while every write blocks: a write on
    /// the calling thread would block the call itself. Then checks each write ran on a thread-pool thread without that context.
    /// </summary>
    private async Task<bool> RunFromUiContextAsync(Func<Task<bool>> operation)
    {
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        _beforeWrite = (_, _) => { started.Set(); gate.Wait(TimeSpan.FromSeconds(10)); };
        var caller = Environment.CurrentManagedThreadId;
        var previous = SynchronizationContext.Current;
        var ui = new MarkerContext();
        Task<bool> running;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            running = operation();
            running.IsCompleted.Should().BeFalse();
            // The caller keeps its thread until the first write has started, so that write cannot borrow it.
            started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        gate.Set();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        _writes.Should().HaveCount(2).And.OnlyContain(w => w.Thread != caller && w.PoolThread && w.Context != ui);
        return result;
    }

    [TestMethod]
    public async Task SaveAsync_MalformedKey_WritesNothingAndReturnsFalse()
    {
        (await _service.SaveAsync("sk-proj-" + Body + Tail)).Should().BeFalse();
        (await _service.SaveAsync(null)).Should().BeFalse();
        _writes.Should().BeEmpty();
        _keyChanged.Should().Be(0);
    }

    [TestMethod]
    public async Task SaveAsync_UserWriteFails_ReturnsFalseLeavesProcessAloneAndLogsTypeOnly()
    {
        _beforeWrite = (value, target) =>
        {
            if (target == EnvironmentVariableTarget.User) throw new System.Security.SecurityException("denied " + value);
        };

        (await _service.SaveAsync(Key)).Should().BeFalse();
        _writes.Should().BeEmpty();
        _processKey.Should().BeNull();
        _keyChanged.Should().Be(0);
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Text.Contains("SecurityException"));
        LogsShouldNotLeakKey();
    }

    [TestMethod]
    public async Task RemoveAsync_KeySet_DeletesUserThenProcessOffTheCallingThread()
    {
        _processKey = Key; _userKey = Key;

        (await RunFromUiContextAsync(_service.RemoveAsync)).Should().BeTrue();

        _writes.Select(w => (w.Name, w.Value, w.Target)).Should().Equal(
            (Name, (string?)null, EnvironmentVariableTarget.User), (Name, (string?)null, EnvironmentVariableTarget.Process));
        _keyChanged.Should().Be(1);
        _service.GetStatus().Should().Be(new AdminKeyStatus(false, null));
    }

    [TestMethod]
    public async Task RemoveAsync_WriteFails_ReturnsFalseWithoutKeyChanged()
    {
        _userKey = Key;
        _beforeWrite = (_, _) => throw new UnauthorizedAccessException(Key);
        (await _service.RemoveAsync()).Should().BeFalse();
        _keyChanged.Should().Be(0);
        LogsShouldNotLeakKey();
    }

    [TestMethod]
    public async Task SaveAsync_KeyChangedHandlerThrows_StillReportsSaved()
    {
        _service.KeyChanged += (_, _) => throw new InvalidOperationException(Key);
        (await _service.SaveAsync(Key)).Should().BeTrue();
        LogsShouldNotLeakKey();
    }

    [TestMethod]
    public async Task AllOperations_EveryOutcome_NeverLogKey()
    {
        _userKey = Key;
        _service.GetStatus();
        foreach (var status in new[] { 200, 401, 403, 429, 500, 404 })
        {
            Status((HttpStatusCode)status);
            await _service.TestAsync(Key);
            await _service.TestSavedAsync();
        }
        _handler.Respond = (_, _) => throw new HttpRequestException(Key);
        await _service.TestAsync(Key);
        await _service.TestAsync("bad " + Key);
        await _service.SaveAsync(Key);
        await _service.RemoveAsync();
        _beforeWrite = (_, _) => throw new IOException(Key);
        await _service.SaveAsync(Key);
        await _service.RemoveAsync();

        _logger.Entries.Should().NotBeEmpty();
        LogsShouldNotLeakKey();
    }

    // Together with the billing service

    [TestMethod]
    public async Task SaveThenRemove_BillingServiceInSameProcess_UsesNewKeyAndForgetsRemovedOne()
    {
        var settings = new Mock<ISettingsService>(); settings.SetupGet(s => s.Settings).Returns(new AppSettings());
        var billingLogger = new CapturingLogger<ApiBillingService>();
        var billingHandler = new Handler
        {
            Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/cost_report", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"data":[{"results":[{"amount":"1200","currency":"USD"}]}],"has_more":false}""") }
                : new HttpResponseMessage(HttpStatusCode.Forbidden))
        };
        using var http = new HttpClient(billingHandler);
        using var billing = new ApiBillingService(settings.Object, billingLogger, http, null,
            (_, target) => target == EnvironmentVariableTarget.Process ? _processKey : _userKey);

        (await billing.GetAsync(false, default)).State.Should().Be(ApiCreditsState.NoKey);
        (await _service.SaveAsync(Key)).Should().BeTrue();
        var saved = await billing.GetAsync(false, default);
        saved.State.Should().Be(ApiCreditsState.Ok);
        saved.SpentUsd.Should().Be(12m);
        billingHandler.Keys.Should().OnlyContain(k => k == Key);

        (await _service.RemoveAsync()).Should().BeTrue();
        var removed = await billing.GetAsync(false, default);
        removed.State.Should().Be(ApiCreditsState.NoKey);
        removed.SpentUsd.Should().Be(0);
    }
}
