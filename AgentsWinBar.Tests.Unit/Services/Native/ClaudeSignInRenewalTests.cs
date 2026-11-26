using System.Net;
using AwesomeAssertions;
using AgentsWinBar.Services.Native;
using static AgentsWinBar.Tests.Unit.Services.Native.NativeTestSupport;

namespace AgentsWinBar.Tests.Unit.Services.Native;

/// <summary>
/// Part C: an expired Claude sign-in is renewed by Claude Code itself (claude auth status --json). Claude Code never runs
/// here: the client tests use a fake renewer, and the runner tests use cmd.exe as a harmless stand-in.
/// </summary>
[TestClass]
public class ClaudeSignInRenewalTests
{
    private const string SyntheticToken = "synthetic-claude-access-token";
    private const string RenewedToken = "synthetic-renewed-access-token";
    private const string SyntheticEmail = "synthetic-user@example.com";
    private const string UsageJson = """{"five_hour": {"utilization": 37.0, "resets_at": "2026-11-25T08:29:59Z"}}""";

    private string _path = null!;

    [TestInitialize]
    public void Setup() => _path = TempFile(Credentials(SyntheticToken, Now.AddHours(-1)));

    [TestCleanup]
    public void Cleanup() => DeleteTemp(_path);

    private static string Credentials(string token, DateTimeOffset expiresAt) => $$$"""
        {"claudeAiOauth": {"accessToken": "{{{token}}}", "expiresAt": {{{expiresAt.ToUnixTimeMilliseconds()}}}, "subscriptionType": "max"}}
        """;

    private (ClaudeNativeClient Client, RecordingHandler Handler, TestClock Clock, ListLogger<ClaudeNativeClient> Logger) Create(
        FakeRenewer renewer, bool enabled = true, CancellationToken shutdown = default)
    {
        var handler = new RecordingHandler(_ => Json(UsageJson));
        var clock = new TestClock(Now);
        var logger = new ListLogger<ClaudeNativeClient>();
        var client = new ClaudeNativeClient(handler.Client(), logger,
            new ClaudeNativeOptions { CredentialsPath = _path, DisplayTimeZone = TimeZoneInfo.Utc, RenewExpiredSignIn = () => enabled, ShutdownToken = shutdown },
            clock, renewer: renewer);
        return (client, handler, clock, logger);
    }

    [TestMethod]
    public async Task FetchAsync_WhenSignInExpiredAndRenewalSucceeds_ReadsUsageInTheSameFetch()
    {
        var renewer = new FakeRenewer(_ => File.WriteAllText(_path, Credentials(RenewedToken, Now.AddHours(8))));
        var (client, handler, _, _) = Create(renewer);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.Ok);
        result.Primary!.UsedPercent.Should().Be(37);
        renewer.Runs.Should().Be(1);
        handler.Requests.Should().ContainSingle().Which.Headers.Authorization!.Parameter.Should().Be(RenewedToken);
    }

    [TestMethod]
    public async Task FetchAsync_WhenClaudeCodeIsMissing_ReturnsSignInExpiredWithoutRunningAnything()
    {
        var renewer = new FakeRenewer(executable: null);
        var (client, handler, _, logger) = Create(renewer);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        renewer.Runs.Should().Be(0);
        handler.Requests.Should().BeEmpty();
        logger.Messages.Should().Contain(m => m.Contains("not found"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FetchAsync_WhenOnlyNpmShimIsOnAbsolutePath_ExplainsManualRenewal(bool enabled)
    {
        var folder = Directory.CreateTempSubdirectory("fake-npm-");
        try
        {
            // Empty file used solely for existence checks.
            File.WriteAllText(Path.Combine(folder.FullName, "claude.cmd"), string.Empty);
            var runner = new ClaudeAuthStatusRunner([], () => $"relative;\"{folder.FullName}\"", () => folder.FullName);
            var handler = new RecordingHandler(_ => Json(UsageJson));
            var client = new ClaudeNativeClient(handler.Client(), new ListLogger<ClaudeNativeClient>(),
                new ClaudeNativeOptions { CredentialsPath = _path, RenewExpiredSignIn = () => enabled },
                new TestClock(Now), renewer: runner);

            var result = await client.FetchAsync();

            result.Status.Should().Be(NativeUsageStatus.SignInExpired);
            result.Message.Should().Contain("cannot be renewed in the background")
                .And.Contain("Open Claude Code on Windows to renew it.");
            handler.Requests.Should().BeEmpty();
        }
        finally
        {
            File.Delete(Path.Combine(folder.FullName, "claude.cmd"));
            folder.Delete();
        }
    }

    [TestMethod]
    public async Task FetchAsync_WhenApplicationShutsDown_CancelsInFlightRenewal()
    {
        using var shutdown = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewer = new FakeRenewer(gate: release.Task);
        var (client, handler, _, _) = Create(renewer, shutdown: shutdown.Token);

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
        var renewer = new FakeRenewer(timedOut: true);
        var (client, handler, _, logger) = Create(renewer);

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
        var renewer = new FakeRenewer();
        var (client, _, clock, _) = Create(renewer);

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
        var renewer = new FakeRenewer();
        var (client, _, clock, _) = Create(renewer);

        await client.FetchAsync();
        clock.Now = Now.AddMinutes(30);
        await client.FetchAsync();
        File.WriteAllText(_path, Credentials(RenewedToken, Now.AddMinutes(-5)));

        await client.FetchAsync();
        renewer.Runs.Should().Be(3);
        clock.Now = Now.AddMinutes(60);
        await client.FetchAsync();
        renewer.Runs.Should().Be(4);
    }

    [TestMethod]
    public async Task FetchAsync_WhenRenewalWasTriedWithin30Minutes_DoesNotRunItAgain()
    {
        var renewer = new FakeRenewer(exitCode: 1); // runs, but the sign-in stays expired
        var (client, _, clock, _) = Create(renewer);

        await client.FetchAsync();
        clock.Now = Now.AddMinutes(29);
        var throttled = await client.FetchAsync();
        clock.Now = Now.AddMinutes(30);
        await client.FetchAsync();

        throttled.Status.Should().Be(NativeUsageStatus.SignInExpired);
        renewer.Runs.Should().Be(2, "one attempt at the start and the next only after 30 minutes");
    }

    [TestMethod]
    public async Task FetchAsync_WhenRenewalSettingIsOff_DoesNotRun()
    {
        var renewer = new FakeRenewer(_ => File.WriteAllText(_path, Credentials(RenewedToken, Now.AddHours(8))));
        var (client, handler, _, _) = Create(renewer, enabled: false);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        renewer.Runs.Should().Be(0);
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchAsync_WhenRenewalLeavesTheSameExpiry_ReturnsSignInExpired()
    {
        var renewer = new FakeRenewer();
        var (client, handler, _, _) = Create(renewer);

        var result = await client.FetchAsync();

        result.Status.Should().Be(NativeUsageStatus.SignInExpired);
        renewer.Runs.Should().Be(1);
        handler.Requests.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FetchAsync_WhenRenewalRuns_LogsOnlyExitCodeAndDuration()
    {
        var renewer = new FakeRenewer(_ => File.WriteAllText(_path, Credentials(RenewedToken, Now.AddHours(8))));
        var (client, _, _, logger) = Create(renewer);

        await client.FetchAsync();

        logger.Messages.Should().Contain(m => m.Contains("exit code 0"));
        logger.Messages.Should().NotContain(m => m.Contains('@') || m.Contains(SyntheticToken) || m.Contains(RenewedToken));
        typeof(ClaudeAuthStatusRun).GetProperties().Should().NotContain(p => p.PropertyType == typeof(string),
            "a run result has nowhere to keep the command's output");
    }

    [TestMethod]
    public async Task RunAuthStatusAsync_WhenProcessPrintsAccountEmail_DiscardsItAndReportsExitCode()
    {
        var runner = new ClaudeAuthStatusRunner(["/d", "/c", "echo " + SyntheticEmail], () => null, () => Path.GetTempPath());

        var run = await runner.RunAuthStatusAsync(CommandShell(), TimeSpan.FromSeconds(20), CancellationToken.None);

        run.TimedOut.Should().BeFalse();
        run.ExitCode.Should().Be(0);
        run.ToString().Should().NotContain(SyntheticEmail);
    }

    [TestMethod]
    public async Task RunAuthStatusAsync_WhenProcessOutlivesTimeout_StopsItAndReportsTimedOut()
    {
        var runner = new ClaudeAuthStatusRunner(["/d", "/c", "ping -n 30 127.0.0.1 >nul"], () => null, () => Path.GetTempPath());

        var run = await runner.RunAuthStatusAsync(CommandShell(), TimeSpan.FromSeconds(1), CancellationToken.None);

        run.TimedOut.Should().BeTrue();
        run.ExitCode.Should().BeNull();
        run.Duration.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    public void FindClaudeExecutable_WhenOnlyRelativePathEntryHasIt_UsesTheNativeInstallFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentswinbar-renewal-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var profile = Path.Combine(root, "profile");
            var native = Path.Combine(profile, ".local", "bin", "claude.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(native)!);
            File.WriteAllText(native, string.Empty);
            var emptyFolder = Path.Combine(root, "empty");
            Directory.CreateDirectory(emptyFolder);

            var runner = new ClaudeAuthStatusRunner(["auth"], () => "relative\\bin" + Path.PathSeparator + emptyFolder, () => profile);

            runner.FindClaudeExecutable().Should().Be(native);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    [TestMethod]
    public void FindClaudeExecutable_WhenOnPath_PrefersPathAndReturnsNullWhenMissingEverywhere()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentswinbar-renewal-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var onPath = Path.Combine(root, "tools", "claude.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(onPath)!);
            File.WriteAllText(onPath, string.Empty);
            var profile = Path.Combine(root, "profile");

            new ClaudeAuthStatusRunner(["auth"], () => Path.GetDirectoryName(onPath), () => profile).FindClaudeExecutable().Should().Be(onPath);
            new ClaudeAuthStatusRunner(["auth"], () => null, () => profile).FindClaudeExecutable().Should().BeNull();
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    private static string CommandShell() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    private sealed class FakeRenewer(Action<string>? onRun = null, string? executable = @"C:\synthetic\claude.exe",
        int exitCode = 0, bool timedOut = false, Task? gate = null) : IClaudeSignInRenewer
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;
        public int Runs => Volatile.Read(ref _runs);
        public Task Started => _started.Task;
        public List<TimeSpan> Timeouts { get; } = [];

        public bool HasNpmShimOnly() => false;
        public string? FindClaudeExecutable() => executable;

        public async Task<ClaudeAuthStatusRun> RunAuthStatusAsync(string path, TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            lock (Timeouts) Timeouts.Add(timeout);
            _started.TrySetResult();
            if (gate != null) await gate.WaitAsync(ct);
            onRun?.Invoke(path);
            return new ClaudeAuthStatusRun(timedOut ? null : exitCode, TimeSpan.FromSeconds(1.5), timedOut);
        }
    }
}
