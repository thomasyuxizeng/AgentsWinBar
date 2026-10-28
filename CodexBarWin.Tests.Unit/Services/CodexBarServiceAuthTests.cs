using System.Text.Json;
using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;
using CodexBarWin.Tests.Unit.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class CodexBarServiceAuthTests
{
    private readonly MockWslService _wsl = new();
    private readonly MockCacheService _cache = new();
    private CodexBarService Service() => new(_wsl, _cache, new MockSettingsService(),
        new MockSampleDataLoader(), NullLogger<CodexBarService>.Instance);

    private void Respond(WslResult result) => _wsl.CommandHandler = (_, _) => Task.FromResult(result);
    private static WslResult Reading(string provider, bool cached = false, bool auth = false) => new()
    {
        Success = true, Output = JsonSerializer.Serialize(new
        {
            provider, cached, authRequired = auth, authError = auth ? "OAuth access token missing" : null,
            usage = new { updatedAt = DateTime.UtcNow, primary = new { usedPercent = 42 } }
        })
    };
    private static WslResult Failure(string provider, bool success = false, string message = "Refresh token revoked", int code = 3) => new()
    {
        Success = success, ExitCode = success ? 0 : 3,
        Output = JsonSerializer.Serialize(new[] { new { provider, error = new { code, kind = "provider", message } } })
    };

    [TestMethod]
    [DataRow("claude", true)]
    [DataRow("codex", false)]
    [DataRow("gemini", false)]
    public async Task GetUsageAsync_ErrorRow_KeepsNumbersFlagsSignOutAndClearsOnFreshSuccess(string provider, bool successfulExit)
    {
        var service = Service();
        Respond(Reading(provider));
        var good = (await service.GetUsageAsync(provider))!;
        Respond(Failure(provider, successfulExit));
        var signedOut = (await service.GetUsageAsync(provider))!;
        signedOut.AuthRequired.Should().BeTrue();
        signedOut.AuthMessage.Should().Be("Refresh token revoked");
        signedOut.UsageIsStale.Should().BeTrue();
        signedOut.Session!.Percent.Should().Be(42);
        signedOut.LastSuccessfulFetch.Should().Be(good.LastSuccessfulFetch);
        _cache.Get(provider)!.AuthRequired.Should().BeFalse();
        _cache.Get(provider)!.AuthMessage.Should().BeNull();
        Respond(new() { ExitCode = 1, Error = "Network error" });
        (await service.GetUsageAsync(provider))!.AuthRequired.Should().BeTrue();
        Respond(Reading(provider, cached: true));
        (await service.GetUsageAsync(provider))!.AuthRequired.Should().BeTrue();
        Respond(Reading(provider));
        var recovered = (await service.GetUsageAsync(provider))!;
        recovered.AuthRequired.Should().BeFalse();
        recovered.AuthMessage.Should().BeNull();
        recovered.UsageIsStale.Should().BeFalse();
        recovered.Error.Should().BeNull();
    }

    [TestMethod]
    public async Task GetUsageAsync_AnnotatedShimCacheWithinTtl_MarksStaleAndDoesNotPersistAuth()
    {
        Respond(Reading("claude", cached: true, auth: true));
        var result = (await Service().GetUsageAsync("claude"))!;
        result.AuthRequired.Should().BeTrue();
        result.AuthMessage.Should().Be("OAuth access token missing");
        result.Session!.Percent.Should().Be(42);
        result.UsageIsStale.Should().BeTrue();
        _cache.Get("claude")!.AuthRequired.Should().BeFalse();
        _cache.Get("claude")!.AuthMessage.Should().BeNull();
    }

    [TestMethod]
    [DataRow("claude")]
    [DataRow("codex")]
    [DataRow("gemini")]
    public async Task GetUsageAsync_AuthFailureWithoutCache_ReportsSignOutWithoutInventingNumbers(string provider)
    {
        Respond(Failure(provider));
        var result = (await Service().GetUsageAsync(provider))!;
        result.AuthRequired.Should().BeTrue();
        result.HasUsage.Should().BeFalse();
        result.HasError.Should().BeTrue();
    }

    [TestMethod]
    [DataRow(1, "Please sign in to Antigravity", true)]
    [DataRow(127, "Run agy to authenticate: command not found", false)]
    [DataRow(124, "Please sign in", false)]
    [DataRow(429, "Rate limited", false)]
    [DataRow(503, "Service outage", false)]
    public async Task GetUsageAsync_StderrFailure_ClassifiesFinalFailure(int exitCode, string error, bool expected)
    {
        Respond(new() { ExitCode = exitCode, Error = error });
        var result = (await Service().GetUsageAsync("gemini"))!;
        result.AuthRequired.Should().Be(expected);
        result.CommandNotFound.Should().Be(exitCode == 127);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task GetUsageAsync_SupersededRequest_CannotSetOrClearNewerAuthState(bool olderFails)
    {
        var service = Service();
        Respond(Reading("claude"));
        await service.GetUsageAsync("claude");
        var older = new TaskCompletionSource<WslResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newer = new TaskCompletionSource<WslResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _wsl.CommandHandler = (_, _) => ++calls == 1 ? older.Task : newer.Task;
        var first = service.GetUsageAsync("claude");
        var second = service.GetUsageAsync("claude");
        newer.SetResult(olderFails ? Reading("claude") : Failure("claude"));
        var current = (await second)!;
        older.SetResult(olderFails ? Failure("claude") : Reading("claude"));
        var late = (await first)!;
        current.AuthRequired.Should().Be(!olderFails);
        late.AuthRequired.Should().Be(current.AuthRequired);
        late.AuthMessage.Should().Be(current.AuthMessage);
        Respond(Reading("claude", cached: true));
        (await service.GetUsageAsync("claude"))!.AuthRequired.Should().Be(current.AuthRequired);
    }

    [TestMethod]
    [DataRow(125)]
    [DataRow(127)]
    [DataRow(429)]
    [DataRow(503)]
    public async Task GetUsageAsync_InfrastructureExitWithAuthJson_DoesNotInferSignOut(int exitCode)
    {
        Respond(Failure("claude") with { ExitCode = exitCode });
        (await Service().GetUsageAsync("claude"))!.AuthRequired.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetAllUsageAsync_BatchErrorRows_ClassifiesEachProvider()
    {
        _wsl.CommandHandler = (command, _) => Task.FromResult(Failure(
            command.Contains("--provider claude") ? "claude" : command.Contains("--provider codex") ? "codex" : "gemini"));
        var results = await Service().GetAllUsageAsync();
        results.Should().HaveCount(3).And.OnlyContain(d => d.AuthRequired && d.AuthMessage == "Refresh token revoked");
    }

    [TestMethod]
    public async Task GetUsageAsync_RateLimitOrStringError_DoesNotSetAuth()
    {
        Respond(Failure("claude", message: "Usage API rate limited (429)", code: 429));
        var service = Service();
        (await service.GetUsageAsync("claude"))!.AuthRequired.Should().BeFalse();
        Respond(new() { Success = true, Output = """[{"provider":"claude","error":"Unexpected failure"}]""" });
        var result = (await service.GetUsageAsync("claude"))!;
        result.Error.Should().Be("Unexpected failure");
        result.AuthRequired.Should().BeFalse();
    }
}

