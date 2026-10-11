using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class DashboardSnapshotBuilderEarlyResetTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Build_WhenNoActiveEarlyResets_SetsPropertiesToNull()
    {
        var provider = new UsageData
        {
            Provider = "codex",
            Session = new UsageWindow { ReportedPercent = 40, WindowMinutes = 300, ResetAt = Now.AddHours(2) },
            FetchedAt = Now
        };

        var inputs = new DashboardInputs
        {
            Providers = [provider],
            NowUtc = Now
        };

        var snapshot = DashboardSnapshotBuilder.Build(inputs);
        var codex = snapshot.Providers.Single(p => p.Id == "codex");
        codex.EarlyResetAtUtc.Should().BeNull();
        codex.EarlyResetFromPercent.Should().BeNull();
        codex.EarlyResetToPercent.Should().BeNull();

        var session = codex.Windows.Single(w => w.Key == "session");
        session.EarlyResetAtUtc.Should().BeNull();
        session.EarlyResetFromPercent.Should().BeNull();
        session.EarlyResetToPercent.Should().BeNull();
    }

    [TestMethod]
    public void Build_WhenActiveEarlyResetExists_PopulatesWindowAndProviderFields()
    {
        var resetAt = Now.AddHours(-1);
        var provider = new UsageData
        {
            Provider = "codex",
            Session = new UsageWindow { ReportedPercent = 5, WindowMinutes = 300, ResetAt = Now.AddHours(5) },
            Weekly = new UsageWindow { ReportedPercent = 50, WindowMinutes = 10080, ResetAt = Now.AddDays(4) },
            FetchedAt = Now
        };

        var earlyReset = new EarlyReset(
            Provider: "codex",
            WindowKey: "session",
            WindowTitle: "Session",
            PreviousPercent: 82.5,
            CurrentPercent: 5.0,
            PreviousResetAtUtc: Now.AddHours(2),
            NewResetAtUtc: Now.AddHours(5),
            DetectedAtUtc: resetAt);

        var inputs = new DashboardInputs
        {
            Providers = [provider],
            NowUtc = Now,
            EarlyResets = [earlyReset]
        };

        var snapshot = DashboardSnapshotBuilder.Build(inputs);
        var codex = snapshot.Providers.Single(p => p.Id == "codex");
        codex.EarlyResetAtUtc.Should().Be(resetAt);
        codex.EarlyResetFromPercent.Should().Be(82.5);
        codex.EarlyResetToPercent.Should().Be(5);

        var session = codex.Windows.Single(w => w.Key == "session");
        session.EarlyResetAtUtc.Should().Be(resetAt);
        session.EarlyResetFromPercent.Should().Be(82.5);
        session.EarlyResetToPercent.Should().Be(5);

        var weekly = codex.Windows.Single(w => w.Key == "weekly");
        weekly.EarlyResetAtUtc.Should().BeNull();
        weekly.EarlyResetFromPercent.Should().BeNull();
    }

    [TestMethod]
    public void Build_UsageRisesTo90_EventDestinationAndTooltipStillSay79To3()
    {
        var reset = new EarlyReset("codex", "session", "Session", 79, 3, Now.AddHours(2), Now.AddHours(5), Now.AddHours(-1));
        var snapshot = DashboardSnapshotBuilder.Build(new()
        {
            Providers = [new() { Provider = "codex", FetchedAt = Now, Session = new() { ReportedPercent = 90 } }],
            NowUtc = Now, EarlyResets = [reset]
        });
        var provider = snapshot.Providers.Single();
        var window = provider.Windows.Single();
        window.UsedPercent.Should().Be(90);
        provider.EarlyResetToPercent.Should().Be(3);
        var detail = EarlyResetDisplay.Detail(window.EarlyResetAtUtc, window.EarlyResetFromPercent, window.EarlyResetToPercent, Now);
        detail.Should().Contain("79% to 3%");
        EarlyResetDisplay.AppendToTip("Session: 90% used", detail).Should().Contain("79% to 3%");
    }
}
