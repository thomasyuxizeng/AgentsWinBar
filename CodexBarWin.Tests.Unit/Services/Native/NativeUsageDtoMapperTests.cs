using AwesomeAssertions;
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
    public void ToDto_WhenSignInExpiredWithoutReading_FlagsAuthRequired()
    {
        var result = new NativeUsageResult
        {
            Provider = "claude", Status = NativeUsageStatus.SignInExpired, Message = "Claude sign-in expired 12 minutes ago.",
            ExpiredFor = TimeSpan.FromMinutes(12)
        };

        var usage = NativeUsageDtoMapper.ToDto(result).ToUsageData(Now.UtcDateTime);

        usage.AuthRequired.Should().BeTrue();
        usage.AuthMessage.Should().Contain("expired 12 minutes ago");
        usage.UsageFetchOutcome.Should().Be(FetchOutcome.Failed);
        usage.HasUsage.Should().BeFalse();
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
}
