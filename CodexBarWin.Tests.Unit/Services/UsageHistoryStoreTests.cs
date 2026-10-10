using AwesomeAssertions;
using CodexBarWin.Models;
using CodexBarWin.Services;

namespace CodexBarWin.Tests.Unit.Services;

[TestClass]
public class UsageHistoryStoreTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);
    private static UsageHistorySample Sample(double percent = 10, int minutes = 0, string provider = "claude", string key = "session") =>
        new() { Timestamp = Now.AddMinutes(minutes), Provider = provider, WindowKey = key, UsedPercent = percent, ResetsAt = Now.AddHours(4) };

    [TestMethod]
    public void Add_WhenChangesBelowHalfPoint_DedupesUntilHalfPointOrTenMinutes()
    {
        var store = new UsageHistoryStore();
        store.Add(Sample(), Now).Should().BeTrue();
        store.Add(Sample(10.49, 1), Now.AddMinutes(1)).Should().BeFalse();
        store.Add(Sample(10.5, 2), Now.AddMinutes(2)).Should().BeTrue();
        store.Add(Sample(10.5, 11), Now.AddMinutes(11)).Should().BeFalse();
        store.Add(Sample(10.5, 12), Now.AddMinutes(12)).Should().BeTrue();
        store.Samples.Should().HaveCount(3);
    }

    [TestMethod]
    public void Add_WhenResetChanges_RecordsEvenWithSameUsage()
    {
        var store = new UsageHistoryStore(); store.Add(Sample(), Now);
        store.Add(Sample(minutes: 1) with { ResetsAt = Now.AddHours(9) }, Now.AddMinutes(1)).Should().BeTrue();
        store.Add(Sample(minutes: 2) with { ResetsAt = null }, Now.AddMinutes(2)).Should().BeTrue();
        store.Samples.Should().HaveCount(3);
    }

    [TestMethod]
    public void Add_WhenProvidersAndWindowsDiffer_DedupesSeparatelyAndNormalizesCase()
    {
        var store = new UsageHistoryStore();
        store.Add(Sample(provider: "CLAUDE"), Now).Should().BeTrue();
        store.Add(Sample(provider: "codex"), Now).Should().BeTrue();
        store.Add(Sample(key: "weekly"), Now).Should().BeTrue();
        store.Get("CLAUDE", "session").Should().ContainSingle().Which.Provider.Should().Be("claude");
        store.Get("claude", "weekly").Should().ContainSingle();
    }

    [TestMethod]
    [DataRow(-1d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void Add_WhenPercentInvalid_Rejects(double value) =>
        new UsageHistoryStore().Add(Sample(value), Now).Should().BeFalse();

    [TestMethod]
    public void Add_WhenOldFutureOutOfOrderOrInvalidIdentity_Rejects()
    {
        var store = new UsageHistoryStore();
        store.Add(Sample() with { Timestamp = Now.AddDays(-15) }, Now).Should().BeFalse();
        store.Add(Sample(minutes: 1), Now).Should().BeFalse();
        store.Add(Sample(provider: "invalid"), Now).Should().BeFalse();
        store.Add(Sample(key: ""), Now).Should().BeFalse();
        store.Add(Sample(), Now).Should().BeTrue();
        store.Add(Sample(50), Now).Should().BeFalse();
        store.Add(Sample(50, -1), Now).Should().BeFalse();
    }

    [TestMethod]
    public void Prune_WhenFourteenDaysOld_KeepsBoundaryAndUpdatesRevision()
    {
        var store = new UsageHistoryStore();
        store.Add(Sample(), Now); store.Add(Sample(15, 1), Now.AddMinutes(1));
        var revision = store.Revision;
        store.Prune(Now.AddDays(14)).Should().Be(0);
        store.Prune(Now.AddDays(14).AddSeconds(1)).Should().Be(1);
        store.Revision.Should().BeGreaterThan(revision);
    }

    [TestMethod]
    public void Restore_WhenUnsortedAndOld_SortsPrunesAndDoesNotExposeMutableCollection()
    {
        var store = new UsageHistoryStore();
        store.Restore([Sample(30, -1), Sample(10, -10), Sample(1) with { Timestamp = Now.AddDays(-15) }], Now);
        store.Get("claude", "session").Select(s => s.UsedPercent).Should().Equal(10, 30);
        var snapshot = store.Samples;
        store.Add(Sample(50), Now);
        snapshot.Should().HaveCount(2);
        store.Samples.Should().HaveCount(3);
    }

    [TestMethod]
    public void Record_WhenMainTertiaryAndExtraWindowsPresent_RecordsStableKeys()
    {
        var window = new UsageWindow { ReportedPercent = 10.5, ResetAt = Now.AddHours(4) };
        var data = new UsageData { Provider = "gemini", Session = window, Weekly = window, Tertiary = window,
            ExtraWindows = [new() { Key = "3p-5h", Title = "Claude and GPT 5-hour", Window = window },
                new() { Title = " Fable Only ", Window = window }] };
        var store = new UsageHistoryStore();
        store.Record(data, Now).Should().Be(5);
        store.Samples.Select(s => s.WindowKey).Should().Equal("session", "weekly", "tertiary", "extra:3p-5h", "extra:fable only");
        store.Record(data with { ExtraWindows = data.ExtraWindows.Reverse().ToArray() }, Now.AddMinutes(1)).Should().Be(0);
        store.Record(data with { IsLoading = true }, Now.AddMinutes(20)).Should().Be(0);
        store.Record(data with { Error = "Failed" }, Now.AddMinutes(20)).Should().Be(0);
    }

    [TestMethod]
    public void ExtraWindowKey_NullTitle_ReturnsEmptyExtraKey() =>
        UsageHistoryStore.ExtraWindowKey(new() { Title = null! }).Should().Be("extra:");
}
