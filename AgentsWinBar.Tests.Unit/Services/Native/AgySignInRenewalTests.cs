using System.Net;
using System.Reflection;
using System.Text;
using AwesomeAssertions;
using AgentsWinBar.Services.Native;
using static AgentsWinBar.Tests.Unit.Services.Native.NativeTestSupport;

namespace AgentsWinBar.Tests.Unit.Services.Native;

/// <summary>
/// An expired agy sign-in is renewed by agy itself (<c>agy models</c>). agy never runs here: the client tests use a fake
/// renewer and a fake credential source, and the runner tests use cmd.exe as a harmless stand-in.
/// </summary>
[TestClass]
public class AgySignInRenewalTests
{
    private const string ExpiredToken = "synthetic-expired-access-token";
    private const string RenewedToken = "synthetic-renewed-access-token";
    private const string LoadCodeAssistJson = """
        {"currentTier": {"id": "free-tier", "name": "Antigravity Free"}, "allowedTiers": [{"id": "free-tier", "isDefault": true}],
         "cloudaicompanionProject": "synthetic-project-123", "gcpManaged": false}
        """;
    private const string SummaryJson = """
        {"groups": [{"displayName": "Gemini models", "buckets": [
          {"bucketId": "gemini-5h", "window": "5h", "remainingFraction": 0.2, "resetTime": "2026-11-25T09:38:00Z"}]}]}
        """;

    private static string Blob(string token, DateTimeOffset expiry) => $$"""
        {"token": {"access_token": "{{token}}", "token_type": "Bearer", "refresh_token": "synthetic-refresh",
                   "expiry": "{{expiry.UtcDateTime:yyyy-MM-ddTHH:mm:ss}}Z", "expires_in": 3599},
         "auth_method": "consumer", "id_token_email": "someone@gmail.com"}
        """;

    private static HttpResponseMessage Respond(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/v1internal:loadCodeAssist" => Json(LoadCodeAssistJson),
        "/v1internal:retrieveUserQuotaSummary" => Json(SummaryJson),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
    };

    private static (AntigravityNativeClient Client, RecordingHandler Handler, TestClock Clock, ListLogger<AntigravityNativeClient> Logger) Create(
        MutableCredentialSource source, FakeRenewer renewer, bool enabled = true, CancellationToken shutdown = default)
    {
        var handler = new RecordingHandler(Respond);
        var clock = new TestClock(Now);
        var logger = new ListLogger<AntigravityNativeClient>();
        var client = new AntigravityNativeClient(handler.Client(), logger, source,
            new AntigravityNativeOptions { RenewExpiredSignIn = () => enabled, ShutdownToken = shutdown }, clock, renewer);
        return (client, handler, clock, logger);
    }

    private static MutableCredentialSource ExpiredSource() => new(Blob(ExpiredToken, Now.AddMinutes(-30)));

    private static Action RenewedBy(MutableCredentialSource source) => () => source.Json = Blob(RenewedToken, Now.AddHours(1));

    [TestMethod]
    public async Task FetchAsync_WhenEarlyRenewalDoesNotExtendExpiry_UsesValidTokenAndRetriesAtExpiry()
    {
        var expiry = Now.AddSeconds(30);
        var source = new MutableCredentialSource(Blob(ExpiredToken, expiry));
        var canRenew = false;
        var renewer = new FakeRenewer(() =>
        {
            if (canRenew) RenewedBy(source)();
        });
        var (client, handler, clock, _) = Create(source, renewer);

        (await client.FetchAsync()).Status.Should().Be(NativeUsageStatus.Ok);
        renewer.Runs.Should().Be(1);
        handler.Requests.Should().OnlyContain(r => r.Headers.Authorization!.Parameter == ExpiredToken);

        clock.Now = expiry;
        canRenew = true;
        (await client.FetchAsync()).Status.Should().Be(NativeUsageStatus.Ok);
        renewer.Runs.Should().Be(2);
        handler.Requests.Last().Headers.Authorization!.Parameter.Should().Be(RenewedToken);
    }

    [TestMethod]
    public async Task FetchAsync_WhenSignInExpiredAndRenewalSucceeds_ContinuesWithTheRenewedToken()
    {
        var source = ExpiredSource();
        var renewer = new FakeRenewer(RenewedBy(source));
        var (client, handler, _, _) = Create(source, renewer);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Ok);
        result.Primary!.UsedPercent.Should().BeApproximately(80, 1e-9);
        renewer.Runs.Should().Be(1);
        handler.Requests.Should().NotBeEmpty();
        handler.Requests.Should().OnlyContain(r => r.Headers.Authorization!.Parameter == RenewedToken);
    }

    [TestMethod]
    public async Task FetchAsync_WhenRenewalSettingIsOff_ReturnsSignInExpiredWithoutRunningAnything()
    {
        var source = ExpiredSource();
        var renewer = new FakeRenewer(RenewedBy(source));
        var (client, handler, _, _) = Create(source, renewer, enabled: false);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        result.Message.Should().Contain("Run agy on Windows");
        renewer.Runs.Should().Be(0);
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchAsync_WhenNoRenewerIsConfigured_ReturnsSignInExpired()
    {
        var handler = new RecordingHandler(Respond);
        var client = new AntigravityNativeClient(handler.Client(), new ListLogger<AntigravityNativeClient>(), ExpiredSource(),
            new AntigravityNativeOptions { RenewExpiredSignIn = () => true }, new TestClock(Now));

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchAsync_WhenAgyIsNotFound_ReturnsSignInExpiredWithoutRunningAnything()
    {
        var source = ExpiredSource();
        var renewer = new FakeRenewer(RenewedBy(source), executable: null);
        var (client, handler, _, logger) = Create(source, renewer);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        renewer.Runs.Should().Be(0);
        handler.Requests.Should().BeEmpty();
        logger.Messages.Should().Contain(m => m.Contains("not found"));
    }

    [TestMethod]
    public async Task FetchAsync_WhenRenewalLeavesTheSameExpiry_ReturnsSignInExpired()
    {
        var source = ExpiredSource();
        var renewer = new FakeRenewer();
        var (client, handler, _, _) = Create(source, renewer);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        renewer.Runs.Should().Be(1);
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchAsync_WhenRenewalMovesExpiryButStillInThePast_ReturnsSignInExpired()
    {
        var source = ExpiredSource();
        var renewer = new FakeRenewer(() => source.Json = Blob(RenewedToken, Now.AddMinutes(-10)));
        var (client, handler, _, _) = Create(source, renewer);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchAsync_WhenApplicationShutsDown_CancelsInFlightRenewal()
    {
        using var shutdown = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewer = new FakeRenewer(gate: release.Task);
        var (client, handler, _, _) = Create(ExpiredSource(), renewer, shutdown: shutdown.Token);

        var fetch = client.FetchAsync();
        try
        {
            await renewer.Started.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown.Cancel();
            var result = await fetch.WaitAsync(TimeSpan.FromSeconds(2));
            result.Status.Should().Be(NativeUsageStatus.SignInExpired);
            handler.Requests.Should().BeEmpty();
        }
        finally
        {
            release.TrySetResult();
            await fetch.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task FetchAsync_WhenRenewalTimesOut_ReturnsSignInExpired()
    {
        var source = ExpiredSource();
        var renewer = new FakeRenewer(timedOut: true);
        var (client, handler, _, logger) = Create(source, renewer);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        renewer.Runs.Should().Be(1);
        renewer.Timeouts.Should().Equal(TimeSpan.FromSeconds(30));
        handler.Requests.Should().BeEmpty();
        logger.Messages.Should().Contain(m => m.Contains("timed out"));
    }

    [TestMethod]
    public async Task FetchAsync_WhenRenewalsKeepFailing_BacksOffAndCapsAtSixHours()
    {
        var source = ExpiredSource();
        var renewer = new FakeRenewer();
        var (client, _, clock, _) = Create(source, renewer);

        await client.FetchAsync();
        var attempts = 1;
        foreach (var delay in new[] { TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(6) })
        {
            var next = clock.Now + delay;
            clock.Now = next.AddSeconds(-1);
            await client.FetchAsync();
            renewer.Runs.Should().Be(attempts);
            clock.Now = next;
            await client.FetchAsync();
            renewer.Runs.Should().Be(++attempts);
        }
    }

    [TestMethod]
    public async Task FetchAsync_WhenStoredExpiryChanges_ResetsRenewalBackoff()
    {
        var source = ExpiredSource();
        var renewer = new FakeRenewer();
        var (client, _, clock, _) = Create(source, renewer);

        await client.FetchAsync();
        clock.Now = Now.AddMinutes(30);
        await client.FetchAsync();
        source.Json = Blob(RenewedToken, Now.AddMinutes(-5));

        await client.FetchAsync();
        renewer.Runs.Should().Be(3);
        clock.Now = Now.AddMinutes(60);
        await client.FetchAsync();
        renewer.Runs.Should().Be(4);
    }

    [TestMethod]
    public async Task FetchAsync_WhenRenewalWasTriedWithin30Minutes_DoesNotRunItAgain()
    {
        var source = ExpiredSource();
        var renewer = new FakeRenewer(exitCode: 1); // runs, but the sign-in stays expired
        var (client, _, clock, _) = Create(source, renewer);

        await client.FetchAsync();
        clock.Now = Now.AddMinutes(29);
        var throttled = await client.FetchAsync();
        clock.Now = Now.AddMinutes(30);
        await client.FetchAsync();

        throttled.Status.Should().Be(NativeUsageStatus.SignInExpired);
        renewer.Runs.Should().Be(2, "one attempt at the start and the next only after 30 minutes");
    }

    [TestMethod]
    public async Task FetchAsync_WhenTwoFetchesFindItExpiredAtOnce_ShareOneRenewal()
    {
        var source = ExpiredSource();
        var release = new TaskCompletionSource();
        var renewer = new FakeRenewer(RenewedBy(source), gate: release.Task);
        var (client, _, _, _) = Create(source, renewer);

        var first = client.FetchAsync();
        await renewer.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var second = client.FetchAsync();
        while (source.Reads < 2) await Task.Delay(5); // the second fetch has read the expired sign-in
        await Task.Delay(100); // and has had time to join the attempt in flight
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Should().OnlyContain(r => r.Status == NativeUsageStatus.Ok);
        renewer.Runs.Should().Be(1);
    }

    [TestMethod]
    public async Task FetchAsync_WhenRenewalRuns_LogsOnlyExitCodeAndDuration()
    {
        var source = ExpiredSource();
        var renewer = new FakeRenewer(RenewedBy(source));
        var (client, _, _, logger) = Create(source, renewer);

        await client.FetchAsync();

        logger.Messages.Should().Contain(m => m.Contains("exit code 0"));
        logger.Messages.Should().NotContain(m => m.Contains('@') || m.Contains(ExpiredToken) || m.Contains(RenewedToken));
        typeof(AgyModelsRun).GetProperties().Should().NotContain(p => p.PropertyType == typeof(string),
            "a run result has nowhere to keep the command's output");
    }

    [TestMethod]
    public void Constructor_WhenDefault_RunsOnlyTheModelsCommand()
    {
        var arguments = typeof(AgyModelsRunner).GetField("_arguments", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(new AgyModelsRunner());

        arguments.Should().BeAssignableTo<IReadOnlyList<string>>().Which.Should().Equal("models");
    }

    [TestMethod]
    public async Task RunModelsAsync_WhenProcessPrintsOutput_DiscardsItAndReportsExitCode()
    {
        var runner = new AgyModelsRunner(["/d", "/c", "echo synthetic-output & exit 3"], () => null, () => null);

        var run = await runner.RunModelsAsync(CommandShell(), TimeSpan.FromSeconds(20), CancellationToken.None);

        run.TimedOut.Should().BeFalse();
        run.ExitCode.Should().Be(3);
        run.ToString().Should().NotContain("synthetic-output");
    }

    [TestMethod]
    public async Task RunModelsAsync_WhenProcessOutlivesTimeout_StopsItAndReportsTimedOut()
    {
        var runner = new AgyModelsRunner(["/d", "/c", "ping -n 30 127.0.0.1 >nul"], () => null, () => null);

        var run = await runner.RunModelsAsync(CommandShell(), TimeSpan.FromSeconds(1), CancellationToken.None);

        run.TimedOut.Should().BeTrue();
        run.ExitCode.Should().BeNull();
        run.Duration.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    public void FindAgyExecutable_WhenOnlyRelativePathEntryHasIt_UsesTheWinGetLinksFolder()
    {
        var root = NewRoot();
        try
        {
            var localAppData = Path.Combine(root, "local");
            var link = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "agy.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            File.WriteAllText(link, string.Empty);
            var emptyFolder = Path.Combine(root, "empty");
            Directory.CreateDirectory(emptyFolder);
            // A relative entry resolves against the working folder, so it is ignored even when agy.exe is there.
            var relativeRoot = "agentswinbar-relative-" + Guid.NewGuid().ToString("N");
            var relative = Path.Combine(relativeRoot, "bin");
            Directory.CreateDirectory(relative);
            File.WriteAllText(Path.Combine(relative, "agy.exe"), string.Empty);
            try
            {
                var runner = new AgyModelsRunner(["models"], () => relative + Path.PathSeparator + emptyFolder, () => localAppData);

                runner.FindAgyExecutable().Should().Be(link);
            }
            finally { try { Directory.Delete(relativeRoot, recursive: true); } catch (IOException) { } }
        }
        finally { DeleteRoot(root); }
    }

    [TestMethod]
    public void FindAgyExecutable_WhenOnPath_PrefersPathAndReturnsNullWhenMissingEverywhere()
    {
        var root = NewRoot();
        try
        {
            var onPath = Path.Combine(root, "tools", "agy.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(onPath)!);
            File.WriteAllText(onPath, string.Empty);
            var localAppData = Path.Combine(root, "local");

            new AgyModelsRunner(["models"], () => Path.GetDirectoryName(onPath), () => localAppData).FindAgyExecutable().Should().Be(onPath);
            new AgyModelsRunner(["models"], () => null, () => localAppData).FindAgyExecutable().Should().BeNull();
            new AgyModelsRunner(["models"], () => null, () => null).FindAgyExecutable().Should().BeNull();
        }
        finally { DeleteRoot(root); }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "agentswinbar-agy-renewal-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private static string CommandShell() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    private sealed class MutableCredentialSource(string json) : IAntigravityCredentialSource
    {
        private string _json = json;
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public string Json { get => Volatile.Read(ref _json); set => Volatile.Write(ref _json, value); }

        public SensitiveBuffer? ReadBlob()
        {
            Interlocked.Increment(ref _reads);
            return SensitiveBuffer.From(Encoding.UTF8.GetBytes(Json));
        }
    }

    private sealed class FakeRenewer(Action? onRun = null, string? executable = @"C:\synthetic\agy.exe",
        int exitCode = 0, bool timedOut = false, Task? gate = null) : IAgySignInRenewer
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;
        public int Runs => Volatile.Read(ref _runs);
        public Task Started => _started.Task;
        public List<TimeSpan> Timeouts { get; } = [];

        public string? FindAgyExecutable() => executable;

        public async Task<AgyModelsRun> RunModelsAsync(string path, TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            lock (Timeouts) Timeouts.Add(timeout);
            _started.TrySetResult();
            if (gate != null) await gate.WaitAsync(ct);
            onRun?.Invoke();
            return new AgyModelsRun(timedOut ? null : exitCode, TimeSpan.FromSeconds(1.5), timedOut);
        }
    }
}
