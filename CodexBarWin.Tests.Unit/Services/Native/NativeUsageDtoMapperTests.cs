using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;
using CodexBarWin.Services.Native;
using static CodexBarWin.Tests.Unit.Services.Native.NativeTestSupport;

namespace CodexBarWin.Tests.Unit.Services.Native;

[TestClass]
public class NativeUsageDtoMapperTests
{
    private static NativeUsageResult Gemini() => new()
    {
        Provider = "gemini",
        Source = "remote",
        Status = NativeUsageStatus.Ok,
        Primary = new(80, 300, Now.AddHours(3), "Resets in 3h 0m"),
        Secondary = new(39, 10080, Now.AddDays(4), "Resets in 4d 0h"),
        ExtraWindows =
        [
            new("antigravity-quota-summary-3p-5h", "Claude and GPT 5-hour", new(10, 300, Now.AddHours(3), null)),
            new("antigravity-quota-summary-3p-weekly", "Claude and GPT weekly", new(25, 10080, Now.AddDays(4), "Shared weekly quota"))
        ],
        UpdatedAt = Now
    };

    [TestMethod]
    public void ToDto_WhenGeminiReading_ParsesLikeShimOutput()
    {
        var usage = NativeUsageDtoMapper.ToDto(Gemini()).ToUsageData(Now.UtcDateTime);

        usage.Provider.Should().Be("gemini");
        usage.UsageFetchOutcome.Should().Be(FetchOutcome.Success);
        usage.Session!.Percent.Should().Be(80);
        usage.Session.WindowMinutes.Should().Be(300);
        usage.Session.ResetAt.Should().Be(Now.AddHours(3).UtcDateTime);
        usage.Weekly!.Percent.Should().Be(39);
        usage.ExtraWindows.Select(e => e.Title).Should().Equal("External 5h", "External 7d");
        usage.AuthRequired.Should().BeFalse();
        usage.Error.Should().BeNull();
        usage.UsageIsCached.Should().BeFalse();
    }

    [TestMethod]
    public void ToDto_WhenSignInExpiredWithoutReading_ReturnsQuietNonAuthError()
    {
        var result = new NativeUsageResult
        {
            Provider = "claude", Status = NativeUsageStatus.SignInExpired, Message = "Claude sign-in expired 12 minutes ago.",
            ExpiredFor = TimeSpan.FromMinutes(12)
        };

        var dto = NativeUsageDtoMapper.ToDto(result);
        var usage = dto.ToUsageData(Now.UtcDateTime);

        dto.AuthRequired.Should().BeFalse();
        dto.AuthError.Should().BeNull();
        dto.Error.GetProperty("kind").GetString().Should().Be("provider");
        dto.Error.TryGetProperty("code", out _).Should().BeFalse();
        dto.Error.GetProperty("message").GetString().Should().Be("Sign-in expired; waiting for the provider's own app to renew it");
        usage.AuthRequired.Should().BeFalse();
        usage.AuthMessage.Should().BeNull();
        usage.Error.Should().Be("Sign-in expired; waiting for the provider's own app to renew it");
        usage.UsageFetchOutcome.Should().Be(FetchOutcome.Failed);
        usage.HasUsage.Should().BeFalse();
    }

    [TestMethod]
    [DataRow("claude")]
    [DataRow("codex")]
    [DataRow("gemini")]
    public void ToDto_WhenSignInExpiredWithoutReading_DoesNotTripAuthFailureDetector(string provider)
    {
        var dto = NativeUsageDtoMapper.ToDto(new NativeUsageResult { Provider = provider, Status = NativeUsageStatus.SignInExpired, Message = "Sign-in expired" });

        var kind = dto.Error.GetProperty("kind").GetString();
        var message = dto.Error.GetProperty("message").GetString();

        AuthFailureDetector.IsAuthFailure(provider, null, kind, message).Should().BeFalse();
        AuthFailureDetector.IsAuthFailure(provider, null, null, NativeUsageDtoMapper.SignInExpiredMessage).Should().BeFalse();
    }

    [TestMethod]
    public void ToDto_WhenSignInExpiredWithReading_EmitsCachedRowWithoutErrorOrAuth()
    {
        var readingTime = Now.AddMinutes(-20);
        var expired = Gemini() with
        {
            Status = NativeUsageStatus.SignInExpired, Message = "Antigravity sign-in expired 5 minutes ago.", UpdatedAt = readingTime,
            Cached = true, CacheUpdatedAt = readingTime
        };

        var dto = NativeUsageDtoMapper.ToDto(expired);
        var usage = dto.ToUsageData(Now.UtcDateTime);

        dto.Cached.Should().BeTrue();
        dto.CacheUpdatedAt.Should().Be(readingTime.UtcDateTime);
        dto.AuthRequired.Should().BeFalse();
        dto.AuthError.Should().BeNull();
        dto.Error.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Undefined);
        usage.AuthRequired.Should().BeFalse();
        usage.Error.Should().BeNull();
        usage.UsageIsCached.Should().BeTrue();
        usage.Session!.Percent.Should().Be(80);
        usage.UsageFetchOutcome.Should().Be(FetchOutcome.Success);
    }

    [TestMethod]
    public void ToDto_WhenSignInExpiredWithReadingNotMarkedCached_StillEmitsCachedRowAtReadingTime()
    {
        var expired = Gemini() with { Status = NativeUsageStatus.SignInExpired, UpdatedAt = Now.AddMinutes(-7) };

        var dto = NativeUsageDtoMapper.ToDto(expired);

        dto.Cached.Should().BeTrue();
        dto.CacheUpdatedAt.Should().Be(Now.AddMinutes(-7).UtcDateTime);
        dto.AuthRequired.Should().BeFalse();
    }

    [TestMethod]
    [DataRow(NativeUsageStatus.SignInMissing, "Not signed in to Claude on Windows.")]
    [DataRow(NativeUsageStatus.Unauthorized, "Claude rejected the Windows sign-in (HTTP 401).")]
    public void ToDto_WhenSignedOutOrRejectedWithoutReading_FlagsAuthRequired(NativeUsageStatus status, string message)
    {
        var dto = NativeUsageDtoMapper.ToDto(new NativeUsageResult { Provider = "claude", Status = status, Message = message });
        var usage = dto.ToUsageData(Now.UtcDateTime);

        dto.AuthRequired.Should().BeTrue();
        dto.AuthError.Should().Be(message);
        dto.Error.GetProperty("kind").GetString().Should().Be("auth");
        usage.AuthRequired.Should().BeTrue();
        usage.UsageFetchOutcome.Should().Be(FetchOutcome.Failed);
        usage.HasUsage.Should().BeFalse();
    }

    [TestMethod]
    [DataRow(NativeUsageStatus.SignInMissing)]
    [DataRow(NativeUsageStatus.Unauthorized)]
    public void ToDto_WhenSignedOutOrRejectedWithCachedReading_KeepsReadingAndFlagsAuthRequired(NativeUsageStatus status)
    {
        var cached = Gemini() with { Status = status, Message = "Sign in again.", Cached = true, CacheUpdatedAt = Now };

        var dto = NativeUsageDtoMapper.ToDto(cached);

        dto.AuthRequired.Should().BeTrue();
        dto.AuthError.Should().Be("Sign in again.");
        dto.Usage.Should().NotBeNull();
        dto.Cached.Should().BeTrue();
    }

    [TestMethod]
    public void ToDto_WhenRateLimitedWithCachedReading_ShowsCachedReadingWithoutError()
    {
        var cached = Gemini() with { Provider = "claude", Source = "oauth", Status = NativeUsageStatus.RateLimited, Cached = true, CacheUpdatedAt = Now, HttpStatus = 429 };

        var usage = NativeUsageDtoMapper.ToDto(cached).ToUsageData(Now.AddMinutes(12).UtcDateTime);

        usage.UsageIsCached.Should().BeTrue();
        usage.Error.Should().BeNull();
        usage.AuthRequired.Should().BeFalse();
        usage.UsageFetchOutcome.Should().Be(FetchOutcome.Success);
    }

    [TestMethod]
    public void ToDto_WhenRateLimitedWithoutReading_ReturnsCliStyleError()
    {
        var result = new NativeUsageResult { Provider = "claude", Status = NativeUsageStatus.RateLimited, HttpStatus = 429, Message = "Claude usage is rate limited right now; retrying shortly." };

        var dto = NativeUsageDtoMapper.ToDto(result);
        var usage = dto.ToUsageData(Now.UtcDateTime);

        dto.Error.GetProperty("code").GetInt32().Should().Be(429);
        dto.Error.GetProperty("kind").GetString().Should().Be("provider");
        usage.Error.Should().Contain("rate limited");
        usage.AuthRequired.Should().BeFalse();
        usage.UsageFetchOutcome.Should().Be(FetchOutcome.Failed);
    }

    [TestMethod]
    public void ToDto_WhenProviderInvalid_Throws()
    {
        var act = () => NativeUsageDtoMapper.ToDto(new NativeUsageResult { Provider = "evil; rm -rf" });

        act.Should().Throw<ArgumentException>();
    }

    // Review finding 20.3: an absurd percentage cannot wrap the whole-number copy into a negative value.
    [TestMethod]
    public void ToUsageWindow_WhenUsedPercentExceedsIntRange_DoesNotWrapNegative()
    {
        var window = new UsageWindowDto { UsedPercent = 1e20, WindowMinutes = 300 }.ToUsageWindow()!;

        window.Used.Should().BeGreaterThanOrEqualTo(0);
        window.Percent.Should().Be(1e20);
    }
}
