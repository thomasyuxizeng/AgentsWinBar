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

        var session = codex.Windows.Single(w => w.Key == "session");
        session.EarlyResetAtUtc.Should().BeNull();
        session.EarlyResetFromPercent.Should().BeNull();
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

        var session = codex.Windows.Single(w => w.Key == "session");
        session.EarlyResetAtUtc.Should().Be(resetAt);
        session.EarlyResetFromPercent.Should().Be(82.5);

        var weekly = codex.Windows.Single(w => w.Key == "weekly");
        weekly.EarlyResetAtUtc.Should().BeNull();
        weekly.EarlyResetFromPercent.Should().BeNull();
    }
}
