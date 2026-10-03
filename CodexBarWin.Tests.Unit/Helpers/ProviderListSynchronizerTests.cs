using System.Collections.ObjectModel;
using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class ProviderListSynchronizerTests
{
    private static UsageData Data(string provider, int used = 10) => new()
    {
        Provider = provider,
        Session = new UsageWindow { Used = used, Limit = 100 }
    };

    private static string[] Ids(ObservableCollection<UsageData> list) => list.Select(p => p.Provider).ToArray();

    [TestMethod]
    public void Sync_WhenProviderNewlyEnabled_AddsLoadingPlaceholderInItsPosition()
    {
        var list = new ObservableCollection<UsageData> { Data("claude"), Data("gemini") };

        ProviderListSynchronizer.Sync(list, ["claude", "codex", "gemini"]);

        Ids(list).Should().Equal("claude", "codex", "gemini");
        list[1].IsLoading.Should().BeTrue();
        list[1].Session.Should().BeNull();
    }

    [TestMethod]
    public void Sync_WhenProviderDisabled_RemovesIt()
    {
        var list = new ObservableCollection<UsageData> { Data("claude"), Data("codex"), Data("gemini") };

        ProviderListSynchronizer.Sync(list, ["claude", "gemini"]);

        Ids(list).Should().Equal("claude", "gemini");
    }

    [TestMethod]
    public void Sync_WhenOrderChanged_MovesEntriesAndKeepsTheirData()
    {
        var claude = Data("claude", 42);
        var list = new ObservableCollection<UsageData> { claude, Data("codex"), Data("gemini") };

        ProviderListSynchronizer.Sync(list, ["gemini", "claude", "codex"]);

        Ids(list).Should().Equal("gemini", "claude", "codex");
        list[1].Should().BeSameAs(claude);
    }

    [TestMethod]
    public void Sync_WhenListEmpty_CreatesPlaceholdersForAll()
    {
        var list = new ObservableCollection<UsageData>();

        ProviderListSynchronizer.Sync(list, ["claude", "codex"]);

        Ids(list).Should().Equal("claude", "codex");
        list.Should().OnlyContain(p => p.IsLoading);
    }

    [TestMethod]
    public void Sync_WhenNothingChanged_LeavesTheListUntouched()
    {
        var claude = Data("claude");
        var gemini = Data("gemini");
        var list = new ObservableCollection<UsageData> { claude, gemini };
        var changes = 0;
        list.CollectionChanged += (_, _) => changes++;

        ProviderListSynchronizer.Sync(list, ["claude", "gemini"]);

        changes.Should().Be(0);
        list[0].Should().BeSameAs(claude);
        list[1].Should().BeSameAs(gemini);
    }

    [TestMethod]
    public void Sync_MatchesProviderIdsCaseInsensitively()
    {
        var list = new ObservableCollection<UsageData> { Data("Claude") };

        ProviderListSynchronizer.Sync(list, ["claude"]);

        list.Should().ContainSingle();
    }
}
