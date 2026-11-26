using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class WidgetSignatureTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);

    private static UsageData ProviderWithTwoBars(bool isLoading) => new()
    {
        Provider = "claude",
        IsLoading = isLoading,
        Session = new UsageWindow { ReportedPercent = 25, WindowMinutes = 300, ResetAt = Now.AddHours(2) },
        Weekly = new UsageWindow { ReportedPercent = 40, WindowMinutes = 10080, ResetAt = Now.AddDays(3) }
    };

    private static UsageData ProviderWithMissingBar(bool isLoading) => new()
    {
        Provider = "claude",
        IsLoading = isLoading,
        Session = new UsageWindow { ReportedPercent = 25, WindowMinutes = 300, ResetAt = Now.AddHours(2) }
    };

    [TestMethod]
    public void ComputeTaskbarSignature_WhenBothBarsPresent_IgnoresIsLoadingTransitions()
    {
        var idle = WidgetSignature.ComputeTaskbarSignature([ProviderWithTwoBars(false)], isLight: false, Now);
        var loading = WidgetSignature.ComputeTaskbarSignature([ProviderWithTwoBars(true)], isLight: false, Now);

        loading.Should().Be(idle);
    }

    [TestMethod]
    public void ComputeTaskbarSignature_WhenBarMissing_ReflectsIsLoading()
    {
        var idle = WidgetSignature.ComputeTaskbarSignature([ProviderWithMissingBar(false)], isLight: false, Now);
        var loading = WidgetSignature.ComputeTaskbarSignature([ProviderWithMissingBar(true)], isLight: false, Now);

        loading.Should().NotBe(idle);
    }

    [TestMethod]
    public void ComputeOverlaySignature_WhenBothBarsPresent_IgnoresIsLoadingTransitions()
    {
        var idle = WidgetSignature.ComputeOverlaySignature([ProviderWithTwoBars(false)], Now);
        var loading = WidgetSignature.ComputeOverlaySignature([ProviderWithTwoBars(true)], Now);

        loading.Should().Be(idle);
    }

    [TestMethod]
    public void ComputeOverlaySignature_WhenBarMissing_ReflectsIsLoading()
    {
        var idle = WidgetSignature.ComputeOverlaySignature([ProviderWithMissingBar(false)], Now);
        var loading = WidgetSignature.ComputeOverlaySignature([ProviderWithMissingBar(true)], Now);

        loading.Should().NotBe(idle);
    }
}
