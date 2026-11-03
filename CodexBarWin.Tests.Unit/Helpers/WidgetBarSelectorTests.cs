using AwesomeAssertions;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Tests.Unit.Helpers;

[TestClass]
public class WidgetBarSelectorTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A window with the given usage and time left; defaults to a comfortable, unchanged one.</summary>
    private static UsageWindow Win(double used, int windowMinutes = 10080, double timeElapsedPercent = 50, DateTime? changedAt = null) => new()
    {
        Used = (int)used,
        Limit = 100,
        WindowMinutes = windowMinutes,
        ResetAt = Now.AddMinutes(windowMinutes * (1 - timeElapsedPercent / 100)),
        LastChangedAt = changedAt
    };

    private static ExtraUsageWindow Extra(string title, UsageWindow window) => new() { Title = title, Window = window };

    private static UsageData Claude(UsageWindow? session, UsageWindow? weekly, params ExtraUsageWindow[] extras) => new()
    {
        Provider = "claude",
        Session = session,
        Weekly = weekly,
        ExtraWindows = extras
    };

    private static string?[] Titles(IReadOnlyList<WidgetBar> bars) => bars.Select(b => b.Title).ToArray();

    [TestMethod]
    public void Pick_WhenNothingIsGoingOn_ShowsSessionAndWeekly()
    {
        var data = Claude(Win(10, 300), Win(20), Extra("Fable only", Win(0)));

        var bars = WidgetBarSelector.Pick(data, Now);

        bars.Should().HaveCount(2);
        bars[0].Window.Should().BeSameAs(data.Session);
        bars[1].Window.Should().BeSameAs(data.Weekly);
        Titles(bars).Should().OnlyContain(t => t == null);
    }

    [TestMethod]
    public void Pick_WhenOnlyTwoWindowsExist_ReturnsBoth()
    {
        WidgetBarSelector.Pick(Claude(Win(10, 300), Win(20)), Now).Should().HaveCount(2);
        WidgetBarSelector.Pick(Claude(Win(10, 300), null), Now).Should().HaveCount(1);
        WidgetBarSelector.Pick(new UsageData { Provider = "claude" }, Now).Should().BeEmpty();
    }

    [TestMethod]
    public void Pick_WhenAnExtraWindowIsAtItsLimit_ItIsLeftOutBecauseThereIsLittleLeftToSee()
    {
        var data = Claude(Win(10, 300), Win(20), Extra("Claude and GPT 5-hour", Win(100, 300)));

        var bars = WidgetBarSelector.Pick(data, Now);

        Titles(bars).Should().OnlyContain(t => t == null);
    }

    [TestMethod]
    public void Pick_WhenAMainWindowIsNearItsLimit_ItSinksBelowQuietWindowsWithResourcesLeft()
    {
        var data = Claude(Win(10, 300), Win(95), Extra("Fable only", Win(0)));

        var bars = WidgetBarSelector.Pick(data, Now);

        // The nearly-used-up weekly window makes way for the quiet Fable window
        Titles(bars).Should().Equal(null, "Fable only");
        bars[0].Window.Should().BeSameAs(data.Session);
    }

    [TestMethod]
    public void Pick_WhenEveryWindowIsAtOrNearItsLimit_StillShowsTheBestTwo()
    {
        var data = Claude(Win(100, 300), Win(95));

        WidgetBarSelector.Pick(data, Now).Should().HaveCount(2);
    }

    [TestMethod]
    public void Pick_WhenAnExtraWindowIsAheadOfSchedule_ItIsPromoted()
    {
        // Fable: 60% used with only 20% of the week gone
        var data = Claude(Win(10, 300), Win(20), Extra("Fable only", Win(60, timeElapsedPercent: 20)));

        Titles(WidgetBarSelector.Pick(data, Now)).Should().Equal(null, "Fable only");
    }

    [TestMethod]
    public void Pick_WhenAnExtraWindowJustChanged_ItBeatsMainWindowsThatAreQuiet()
    {
        var data = Claude(Win(10, 300), Win(20), Extra("Fable only", Win(5, changedAt: Now.AddMinutes(-2))));

        Titles(WidgetBarSelector.Pick(data, Now)).Should().Equal(null, "Fable only");
    }

    [TestMethod]
    public void Pick_WhenTheChangeIsOld_TheMainWindowsComeBack()
    {
        var data = Claude(Win(10, 300), Win(20), Extra("Fable only", Win(5, changedAt: Now.AddMinutes(-45))));

        Titles(WidgetBarSelector.Pick(data, Now)).Should().OnlyContain(t => t == null);
    }

    [TestMethod]
    public void Pick_WhenEverythingChangedAtOnce_PrefersTheSpecificWindow()
    {
        // Using a specific model moves the session, the weekly and the model's own window together
        var changed = Now.AddMinutes(-1);
        var data = Claude(Win(10, 300, changedAt: changed), Win(20, changedAt: changed),
            Extra("Fable only", Win(5, changedAt: changed)));

        var bars = WidgetBarSelector.Pick(data, Now);

        bars.Should().Contain(b => b.Title == "Fable only");
    }

    [TestMethod]
    public void Pick_WhenSeveralMainWindowsAreEqual_KeepsSessionAndWeekly()
    {
        var data = Claude(Win(10, 300), Win(20), Extra("A", Win(0)), Extra("B", Win(0)), Extra("C", Win(0)));

        WidgetBarSelector.Pick(data, Now).Select(b => b.Window).Should().Equal(data.Session!, data.Weekly!);
    }

    [TestMethod]
    public void Pick_WhenSeveralExtrasQualify_OnlyTheBestOneTakesASlot()
    {
        // Gemini-style: the Claude/GPT 5-hour window is at its limit (little left to see), the weekly one is
        // ahead of schedule. The row must still show Gemini's own usage, plus the more useful extra.
        var data = Claude(Win(0.1, 300), Win(2),
            Extra("Claude and GPT 5-hour", Win(100, 300)),
            Extra("Claude and GPT weekly", Win(50, timeElapsedPercent: 3)));

        var bars = WidgetBarSelector.Pick(data, Now);

        bars.Should().HaveCount(2);
        bars.Count(b => b.Title != null).Should().Be(1);
        bars.Should().Contain(b => b.Title == "Claude and GPT weekly");
        bars.Should().Contain(b => b.Window == data.Session);
    }

    [TestMethod]
    public void Pick_ExposesTheProviderAccentOfEachBar()
    {
        var data = Claude(Win(10, 300), Win(20), Extra("Fable only", Win(60, timeElapsedPercent: 20)));

        var bars = WidgetBarSelector.Pick(data, Now);

        bars[0].Accent.Should().Be(WindowAccent.None);
        bars[1].Accent.Should().Be(WindowAccent.Claude);
    }

    [TestMethod]
    public void Pick_WhenThereAreNoMainWindows_ExtrasFillTheRow()
    {
        var data = Claude(null, null, Extra("A", Win(10)), Extra("B", Win(20)), Extra("C", Win(30)));

        WidgetBarSelector.Pick(data, Now).Should().HaveCount(2);
    }

    [TestMethod]
    public void Score_RankingsFollowTheDocumentedPriority()
    {
        var changed = WidgetBarSelector.Score(Win(5, changedAt: Now), 3, true, Now);
        var ahead = WidgetBarSelector.Score(Win(60, timeElapsedPercent: 20), 3, true, Now);
        var quiet = WidgetBarSelector.Score(Win(5), 3, true, Now);
        var nearLimit = WidgetBarSelector.Score(Win(95), 3, true, Now);
        var atLimit = WidgetBarSelector.Score(Win(100), 3, true, Now);

        // 1. recent change, 2. ahead of schedule, (quiet), 3. near limit, 4. at limit
        changed.Should().BeGreaterThan(ahead);
        ahead.Should().BeGreaterThan(quiet);
        quiet.Should().BeGreaterThan(nearLimit);
        nearLimit.Should().BeGreaterThan(atLimit);
    }

    [TestMethod]
    public void Score_AWindowThatChangedRecentlyOutranksEvenOneAheadOfSchedule()
    {
        var changedButQuiet = WidgetBarSelector.Score(Win(5, changedAt: Now.AddMinutes(-5)), 1, false, Now);
        var aheadButStill = WidgetBarSelector.Score(Win(60, timeElapsedPercent: 20), 0, false, Now);

        changedButQuiet.Should().BeGreaterThan(aheadButStill);
    }

    [TestMethod]
    public void Score_AnAtLimitWindowThatIsStillChangingIsStillRecent()
    {
        var atLimitChanging = WidgetBarSelector.Score(Win(100, changedAt: Now.AddMinutes(-1)), 1, false, Now);
        var quiet = WidgetBarSelector.Score(Win(5), 0, false, Now);

        atLimitChanging.Should().BeGreaterThan(quiet);
    }

    [TestMethod]
    public void Pick_WhenAnExtraWeeklyIsAtItsLimit_ItReplacesTheUnusableExtraSession()
    {
        var data = Claude(Win(0, 300), Win(93),
            Extra("External 5h", Win(0, 300)), Extra("External 7d", Win(100)));

        var bars = WidgetBarSelector.Pick(data, Now);

        bars.Should().HaveCount(2);
        bars[0].Window.Should().BeSameAs(data.Session);
        bars[1].Title.Should().Be("External 7d");
    }

    [TestMethod]
    public void Pick_WhenTheWeeklyIsAtItsLimit_ItReplacesTheUnusableSession()
    {
        var data = Claude(Win(0, 300), Win(100), Extra("Fable only", Win(0)));

        var bars = WidgetBarSelector.Pick(data, Now);

        bars.Should().HaveCount(2);
        bars[0].Window.Should().BeSameAs(data.Weekly);
        bars[1].Title.Should().Be("Fable only");
    }

    [TestMethod]
    public void Pick_WhenTheSessionIsAlsoUsedUp_NothingIsReplaced()
    {
        var data = Claude(Win(100, 300), Win(100), Extra("Fable only", Win(0)));

        var bars = WidgetBarSelector.Pick(data, Now);

        bars.Select(b => b.Window).Should().Contain(data.Session!);
    }
}
