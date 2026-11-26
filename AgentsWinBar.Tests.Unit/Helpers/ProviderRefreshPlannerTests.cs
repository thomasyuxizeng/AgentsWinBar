using AwesomeAssertions;
using AgentsWinBar.Helpers;
using AgentsWinBar.Models;

namespace AgentsWinBar.Tests.Unit.Helpers;

[TestClass]
public class ProviderRefreshPlannerTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RefreshPlanOptions Options = new() { BaseInterval = TimeSpan.FromSeconds(300) };

    private static ProviderRefreshInput Input(string provider, double? activityMinutesAgo = null, double? fetchMinutesAgo = 0,
        double? percent = null, bool pace = false, int streak = 0, DateTime? limitedUntil = null) =>
        new(provider,
            activityMinutesAgo is { } a ? Now.AddMinutes(-a) : null,
            fetchMinutesAgo is { } f ? Now.AddMinutes(-f) : null,
            percent, pace, streak, limitedUntil);

    private static ProviderRefreshDue Plan(ProviderRefreshInput input, RefreshPlanOptions? options = null) =>
        ProviderRefreshPlanner.PlanProvider(input, options ?? Options, Now);

    [TestMethod]
    [DataRow("codex")]
    [DataRow("gemini")]
    public void PlanProvider_LogsChangedWithinFiveMinutes_RefreshesEveryTwoMinutes(string provider)
    {
        var due = Plan(Input(provider, activityMinutesAgo: 4.9, fetchMinutesAgo: 0.5, streak: 10));

        due.Reason.Should().Be(RefreshReason.Active);
        due.Interval.Should().Be(TimeSpan.FromMinutes(2));
        due.DueUtc.Should().Be(Now.AddMinutes(1.5));
    }

    [TestMethod]
    public void PlanProvider_LogsQuietForMoreThanFiveMinutes_FollowsIdleBackoff()
    {
        var due = Plan(Input("codex", activityMinutesAgo: 5.1, streak: 2));

        due.Reason.Should().Be(RefreshReason.Idle);
        due.BackoffStep.Should().Be(1);
        due.Interval.Should().Be(TimeSpan.FromMinutes(10));
    }

    [TestMethod]
    [DataRow(0, 300, 0)]
    [DataRow(2, 600, 1)]
    [DataRow(4, 900, 2)]
    [DataRow(50, 900, 2)]
    public void PlanProvider_IdleStreak_MatchesAdaptivePolicy(int streak, int seconds, int step)
    {
        var due = Plan(Input("gemini", streak: streak));

        due.Interval.Should().Be(TimeSpan.FromSeconds(seconds));
        due.BackoffStep.Should().Be(step);
    }

    [TestMethod]
    public void PlanProvider_ActiveWithBaseIntervalBelowTwoMinutes_KeepsTheShorterBase()
    {
        var due = Plan(Input("codex", activityMinutesAgo: 1), Options with { BaseInterval = TimeSpan.FromSeconds(60) });

        due.Interval.Should().Be(TimeSpan.FromSeconds(60));
        due.Reason.Should().Be(RefreshReason.Active);
    }

    [TestMethod]
    public void PlanProvider_ClaudeActive_UsesClaudeFloor()
    {
        var due = Plan(Input("claude", activityMinutesAgo: 1, fetchMinutesAgo: 0));

        due.Reason.Should().Be(RefreshReason.Active);
        due.Interval.Should().Be(ProviderRefreshPlanner.ClaudeActiveFloor);
    }

    [TestMethod]
    public void PlanProvider_ClaudeWithShortBaseInterval_NeverGoesBelowFloor()
    {
        var options = Options with { BaseInterval = TimeSpan.FromSeconds(60) };

        Plan(Input("claude", activityMinutesAgo: 1), options).Interval.Should().Be(ProviderRefreshPlanner.ClaudeActiveFloor);
        Plan(Input("claude"), options).Interval.Should().Be(ProviderRefreshPlanner.ClaudeActiveFloor);
        Plan(Input("codex"), options).Interval.Should().Be(TimeSpan.FromSeconds(60));
    }

    [TestMethod]
    public void ClaudeCacheLifetime_IsShorterThanFloor_SoADueRefreshAlwaysCalls()
    {
        ProviderRefreshPlanner.ClaudeCacheLifetime.Should().BeLessThan(ProviderRefreshPlanner.ClaudeActiveFloor);
        ProviderRefreshPlanner.ClaudeCacheLifetime.Should().BePositive();
    }

    [TestMethod]
    [DataRow(85.0, false)]
    [DataRow(99.0, false)]
    [DataRow(10.0, true)]
    public void PlanProvider_IdleNearLimit_RefreshesAtLeastEveryFiveMinutes(double percent, bool pace)
    {
        var due = Plan(Input("codex", percent: percent, pace: pace, streak: 4));

        due.Reason.Should().Be(RefreshReason.NearLimit);
        due.Interval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public void PlanProvider_BelowNearLimit_BacksOff()
    {
        var due = Plan(Input("codex", percent: 84.9, streak: 4));

        due.Reason.Should().Be(RefreshReason.Idle);
        due.Interval.Should().Be(TimeSpan.FromMinutes(15));
    }

    [TestMethod]
    public void PlanProvider_NearLimitWithShorterBase_KeepsBase()
    {
        var due = Plan(Input("codex", percent: 95), Options with { BaseInterval = TimeSpan.FromSeconds(120) });

        due.Interval.Should().Be(TimeSpan.FromSeconds(120));
        due.Reason.Should().Be(RefreshReason.Idle);
    }

    [TestMethod]
    public void PlanProvider_Locked_WaitsAtLeastTheLockedInterval()
    {
        var options = Options with { Locked = true };

        var active = Plan(Input("codex", activityMinutesAgo: 1), options);
        var nearLimit = Plan(Input("claude", percent: 99), options);

        active.Interval.Should().Be(TimeSpan.FromMinutes(30));
        active.Reason.Should().Be(RefreshReason.Locked);
        nearLimit.Interval.Should().Be(TimeSpan.FromMinutes(30));
    }

    [TestMethod]
    public void PlanProvider_LockedIntervalShorterThanIdle_KeepsIdle()
    {
        var due = Plan(Input("codex", streak: 4), Options with { Locked = true, LockedInterval = TimeSpan.FromMinutes(5) });

        due.Interval.Should().Be(TimeSpan.FromMinutes(15));
        due.Reason.Should().Be(RefreshReason.Idle);
    }

    [TestMethod]
    public void PlanProvider_BatterySaver_StretchesEveryState()
    {
        var options = Options with { BatterySaver = true };

        Plan(Input("codex", activityMinutesAgo: 1), options).Interval.Should().Be(TimeSpan.FromMinutes(4));
        Plan(Input("claude", activityMinutesAgo: 1), options).Interval.Should().Be(TimeSpan.FromMinutes(4));
        Plan(Input("codex", percent: 90, streak: 4), options).Interval.Should().Be(TimeSpan.FromMinutes(10));
        Plan(Input("codex", streak: 0), options).Interval.Should().Be(TimeSpan.FromMinutes(10));
        Plan(Input("codex", streak: 4), options).Interval.Should().Be(TimeSpan.FromMinutes(30));
    }

    [TestMethod]
    [DataRow(60, 0, true, false, false)]
    [DataRow(300, 0, true, false, false)]
    [DataRow(300, 2, true, false, false)]
    [DataRow(300, 4, true, false, false)]
    [DataRow(300, 4, false, false, false)]
    [DataRow(600, 3, true, true, false)]
    [DataRow(300, 1, true, false, true)]
    [DataRow(86400, 5, true, true, true)]
    public void PlanProvider_SettingOff_EqualsTodaysDelay(int baseSeconds, int streak, bool adaptive, bool battery, bool locked)
    {
        var options = new RefreshPlanOptions
        {
            BaseInterval = TimeSpan.FromSeconds(baseSeconds), AdaptiveEnabled = adaptive, ActivityRefreshEnabled = false,
            BatterySaver = battery, Locked = locked
        };
        var today = AdaptiveRefreshPolicy.GetDelay(TimeSpan.FromSeconds(baseSeconds), streak, adaptive, battery);
        if (locked && TimeSpan.FromMinutes(30) > today) today = TimeSpan.FromMinutes(30);

        foreach (var provider in new[] { "claude", "codex", "gemini" })
        {
            // Activity and a near limit change nothing while the setting is off.
            var due = Plan(Input(provider, activityMinutesAgo: 0.5, percent: 99, pace: true, streak: streak), options);
            due.Interval.Should().Be(today, provider);
            due.DueUtc.Should().Be(Now + today, provider);
        }
    }

    [TestMethod]
    public void PlanProvider_NeverFetched_IsDueNow()
    {
        var due = Plan(Input("gemini", fetchMinutesAgo: null));

        due.Reason.Should().Be(RefreshReason.First);
        due.DueUtc.Should().Be(Now);
    }

    [TestMethod]
    public void PlanProvider_LastFetchAheadOfClock_WaitsOneIntervalFromNow()
    {
        // The clock was set back an hour after the last fetch.
        var due = Plan(Input("codex", fetchMinutesAgo: -60));

        due.DueUtc.Should().Be(Now.AddMinutes(5));
    }

    [TestMethod]
    public void PlanProvider_ActivityAheadOfClock_CountsAsJustNow()
    {
        var due = Plan(Input("codex", activityMinutesAgo: -30));

        due.Reason.Should().Be(RefreshReason.Active);
    }

    [TestMethod]
    public void PlanProvider_ClockJumpedForward_IsDueAtOnce()
    {
        var due = Plan(Input("codex", fetchMinutesAgo: 600));

        due.DueUtc.Should().BeBefore(Now);
        ProviderRefreshPlanner.SelectDue([due], Now).Should().ContainSingle();
    }

    [TestMethod]
    public void PlanProvider_LocalTimes_AreComparedAsUtc()
    {
        var input = new ProviderRefreshInput("codex", Now.AddMinutes(-1).ToLocalTime(), Now.AddMinutes(-1).ToLocalTime());

        var due = ProviderRefreshPlanner.PlanProvider(input, Options, Now.ToLocalTime());

        due.Reason.Should().Be(RefreshReason.Active);
        due.DueUtc.Should().Be(Now.AddMinutes(1));
        due.DueUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [TestMethod]
    public void PlanProvider_RateLimited_WaitsForCooldownEnd()
    {
        var until = Now.AddMinutes(9);

        var due = Plan(Input("claude", activityMinutesAgo: 1, fetchMinutesAgo: 1, limitedUntil: until));

        due.DueUtc.Should().Be(until);
        due.RateLimited.Should().BeTrue();
        due.Reason.Should().Be(RefreshReason.Active);
    }

    [TestMethod]
    public void PlanProvider_CooldownEndedBeforeDue_KeepsDue()
    {
        var due = Plan(Input("codex", fetchMinutesAgo: 1, limitedUntil: Now.AddMinutes(-1)));

        due.DueUtc.Should().Be(Now.AddMinutes(4));
        due.RateLimited.Should().BeFalse();
    }

    [TestMethod]
    public void Plan_InvalidAndDuplicateProviders_AreSkipped()
    {
        var plan = ProviderRefreshPlanner.Plan(
            [Input("Claude"), Input("not-a-provider"), Input("claude"), Input("codex")], Options, Now);

        plan.Select(p => p.Provider).Should().Equal("claude", "codex");
    }

    [TestMethod]
    public void Plan_ProviderDisabled_IsNotPlanned()
    {
        var plan = ProviderRefreshPlanner.Plan([Input("codex", fetchMinutesAgo: 10)], Options, Now);

        plan.Should().ContainSingle().Which.Provider.Should().Be("codex");
        ProviderRefreshPlanner.SelectDue(plan, Now).Select(p => p.Provider).Should().Equal("codex");
    }

    [TestMethod]
    public void Plan_ProviderAdded_IsDueAtOnceWithoutPullingOthers()
    {
        var plan = ProviderRefreshPlanner.Plan(
            [Input("claude", fetchMinutesAgo: 1), Input("codex", fetchMinutesAgo: 1), Input("gemini", fetchMinutesAgo: null)],
            Options, Now);

        ProviderRefreshPlanner.SelectDue(plan, Now).Select(p => (p.Provider, p.Reason))
            .Should().Equal(("gemini", RefreshReason.First));
        ProviderRefreshPlanner.NextDue(plan).Should().Be(Now);
    }

    [TestMethod]
    public void SelectDue_NothingDue_ReturnsEmpty()
    {
        var plan = ProviderRefreshPlanner.Plan([Input("codex", fetchMinutesAgo: 1)], Options, Now);

        ProviderRefreshPlanner.SelectDue(plan, Now).Should().BeEmpty();
        ProviderRefreshPlanner.NextDue(plan).Should().Be(Now.AddMinutes(4));
    }

    [TestMethod]
    public void SelectDue_ProviderDueWithinCoalesceWindow_IsFetchedTogether()
    {
        var plan = ProviderRefreshPlanner.Plan(
            [Input("codex", fetchMinutesAgo: 5), Input("gemini", fetchMinutesAgo: 4.75), Input("claude", fetchMinutesAgo: 4.95)],
            Options, Now);

        // Gemini is 15 s from due and joins; Claude's floor is strict, so it waits its 3 s.
        ProviderRefreshPlanner.SelectDue(plan, Now).Select(p => p.Provider).Should().Equal("codex", "gemini");
    }

    [TestMethod]
    public void SelectDue_Locked_FetchesEveryProviderAtOnce()
    {
        var options = Options with { Locked = true };
        var plan = ProviderRefreshPlanner.Plan(
            [Input("codex", fetchMinutesAgo: 31), Input("gemini", fetchMinutesAgo: 10)], options, Now);

        ProviderRefreshPlanner.SelectDue(plan, Now, locked: true).Select(p => p.Provider).Should().Equal("codex", "gemini");
    }

    [TestMethod]
    public void SelectDue_RateLimitedProvider_NeverJoinsEarly()
    {
        var plan = ProviderRefreshPlanner.Plan(
            [Input("codex", fetchMinutesAgo: 5), Input("gemini", fetchMinutesAgo: 5, limitedUntil: Now.AddSeconds(10))],
            Options, Now);
        var locked = Options with { Locked = true };
        var lockedPlan = ProviderRefreshPlanner.Plan(
            [Input("codex", fetchMinutesAgo: 31), Input("gemini", fetchMinutesAgo: 31, limitedUntil: Now.AddMinutes(5))],
            locked, Now);

        ProviderRefreshPlanner.SelectDue(plan, Now).Select(p => p.Provider).Should().Equal("codex");
        ProviderRefreshPlanner.SelectDue(lockedPlan, Now, locked: true).Select(p => p.Provider).Should().Equal("codex");
    }

    [TestMethod]
    public void PlanProvider_ClaudeJustFetched_CannotBeReadBeforeItsFloor()
    {
        var claude = Plan(Input("claude", fetchMinutesAgo: 1));
        var codex = Plan(Input("codex", fetchMinutesAgo: 1));

        claude.NotBeforeUtc.Should().Be(Now.AddMinutes(1));
        ProviderRefreshPlanner.MayReadNow(claude, Now).Should().BeFalse();
        ProviderRefreshPlanner.MayReadNow(claude, Now.AddMinutes(1)).Should().BeTrue();
        ProviderRefreshPlanner.MayReadNow(codex, Now).Should().BeTrue();
    }

    [TestMethod]
    public void PlanProvider_CooldownEndingBeforeDue_StillHoldsTheProvider()
    {
        var due = Plan(Input("codex", fetchMinutesAgo: 1, streak: 4, limitedUntil: Now.AddMinutes(5)));

        due.RateLimited.Should().BeFalse("its idle due time is after the cooldown");
        due.CooldownUntilUtc.Should().Be(Now.AddMinutes(5));
        due.NotBeforeUtc.Should().Be(Now.AddMinutes(5));
        ProviderRefreshPlanner.InCooldown(due, Now).Should().BeTrue();
        ProviderRefreshPlanner.MayReadNow(due, Now.AddMinutes(4)).Should().BeFalse();
        ProviderRefreshPlanner.MayReadNow(due, Now.AddMinutes(5)).Should().BeTrue();
    }

    [TestMethod]
    public void PlanProvider_CooldownOver_HoldsNothing()
    {
        var due = Plan(Input("gemini", fetchMinutesAgo: 1, limitedUntil: Now.AddMinutes(-1)));

        due.CooldownUntilUtc.Should().BeNull();
        ProviderRefreshPlanner.MayReadNow(due, Now).Should().BeTrue();
    }

    [TestMethod]
    public void PlanProvider_NeverFetchedButCoolingDown_WaitsForTheCooldown()
    {
        var due = Plan(Input("claude", fetchMinutesAgo: null, limitedUntil: Now.AddMinutes(3)));

        due.NotBeforeUtc.Should().Be(Now.AddMinutes(3));
        ProviderRefreshPlanner.MayReadNow(due, Now).Should().BeFalse();
    }

    [TestMethod]
    public void PlanProvider_SettingOff_HasNoFloor()
    {
        var due = Plan(Input("claude", fetchMinutesAgo: 1), Options with { ActivityRefreshEnabled = false });

        due.NotBeforeUtc.Should().Be(Now.AddMinutes(-1));
        due.Faster.Should().BeFalse();
    }

    [TestMethod]
    public void SelectDue_LockedWithClaudeWithinItsFloor_LeavesClaudeOut()
    {
        // Battery saver: Codex idle at 30 min is due now; Claude was read 95 s ago (its active wait is 4 min)
        var options = Options with { Locked = true, BatterySaver = true };
        var plan = ProviderRefreshPlanner.Plan(
            [Input("codex", fetchMinutesAgo: 30, streak: 4), Input("claude", activityMinutesAgo: 1, fetchMinutesAgo: 95 / 60.0),
             Input("gemini", fetchMinutesAgo: 1)], options, Now);

        ProviderRefreshPlanner.SelectDue(plan, Now, locked: true).Select(p => p.Provider).Should().Equal("codex", "gemini");
        ProviderRefreshPlanner.SelectDue(plan, Now.AddSeconds(26), locked: true).Select(p => p.Provider)
            .Should().Equal("codex", "claude", "gemini");
    }

    [TestMethod]
    public void SelectDue_LockedWithProviderInCooldown_LeavesItOut()
    {
        var options = Options with { Locked = true };
        var plan = ProviderRefreshPlanner.Plan(
            [Input("codex", fetchMinutesAgo: 31), Input("gemini", fetchMinutesAgo: 10, limitedUntil: Now.AddMinutes(5))],
            options, Now);

        plan[1].RateLimited.Should().BeFalse("its locked due time is after the cooldown");
        ProviderRefreshPlanner.SelectDue(plan, Now, locked: true).Select(p => p.Provider).Should().Equal("codex");
    }

    [TestMethod]
    public void SelectDue_DueTolerance_TakesAProviderDueAMomentLater()
    {
        var plan = ProviderRefreshPlanner.Plan([Input("codex", fetchMinutesAgo: 5 - 0.5 / 60)], Options, Now);

        ProviderRefreshPlanner.SelectDue(plan, Now).Should().BeEmpty();
        ProviderRefreshPlanner.SelectDue(plan, Now, dueTolerance: TimeSpan.FromSeconds(1)).Select(p => p.Provider)
            .Should().Equal("codex");
    }

    [TestMethod]
    public void SelectDue_DueTolerance_NeverEndsACooldownEarly()
    {
        var tolerance = TimeSpan.FromSeconds(1);
        var limited = ProviderRefreshPlanner.Plan(
            [Input("gemini", fetchMinutesAgo: 5, limitedUntil: Now.AddMilliseconds(500))], Options, Now);
        // The Codex cooldown ends 0.2 s before its own due time, both within the tolerance
        var cooling = ProviderRefreshPlanner.Plan(
            [Input("codex", fetchMinutesAgo: 5 - 0.5 / 60, limitedUntil: Now.AddMilliseconds(300))], Options, Now);

        limited[0].RateLimited.Should().BeTrue();
        ProviderRefreshPlanner.SelectDue(limited, Now, dueTolerance: tolerance).Should().BeEmpty();
        ProviderRefreshPlanner.SelectDue(limited, Now.AddMilliseconds(500), dueTolerance: tolerance).Should().ContainSingle();
        cooling[0].RateLimited.Should().BeFalse();
        ProviderRefreshPlanner.SelectDue(cooling, Now, dueTolerance: tolerance).Should().BeEmpty();
        ProviderRefreshPlanner.SelectDue(cooling, Now.AddMilliseconds(300), dueTolerance: tolerance).Should().ContainSingle();
    }

    [TestMethod]
    public void SelectDue_CoalesceWindow_LeavesOutAProviderInCooldown()
    {
        var plan = ProviderRefreshPlanner.Plan(
            [Input("codex", fetchMinutesAgo: 5), Input("gemini", fetchMinutesAgo: 5 - 10 / 60.0, limitedUntil: Now.AddSeconds(5))],
            Options, Now);

        plan[1].RateLimited.Should().BeFalse();
        ProviderRefreshPlanner.SelectDue(plan, Now).Select(p => p.Provider).Should().Equal("codex");
    }

    [TestMethod]
    public void IsActive_AtTheBoundary_IsInclusive()
    {
        ProviderRefreshPlanner.IsActive(Now - ProviderRefreshPlanner.ActiveWindow, Now).Should().BeTrue();
        ProviderRefreshPlanner.IsActive(Now - ProviderRefreshPlanner.ActiveWindow - TimeSpan.FromTicks(1), Now).Should().BeFalse();
        ProviderRefreshPlanner.IsActive(null, Now).Should().BeFalse();
    }

    [TestMethod]
    public void Pressure_TakesHighestNumericWindowAndPaceWarning()
    {
        var data = new UsageData
        {
            Provider = "codex",
            Session = new UsageWindow { ReportedPercent = 40, WindowMinutes = 300, ResetAt = Now.AddHours(4) },
            Weekly = new UsageWindow { ReportedPercent = 86, WindowMinutes = 10080, ResetAt = Now.AddDays(6) },
            ExtraWindows = [new() { Key = "x", Window = new UsageWindow() }]
        };

        var (percent, pace) = ProviderRefreshPlanner.Pressure(data, Now);

        percent.Should().Be(86);
        // 86% used one day into a seven-day window runs out long before the reset
        pace.Should().BeTrue();
    }

    [TestMethod]
    public void Pressure_NoNumericPercentOrSignedOut_IsUnknown()
    {
        ProviderRefreshPlanner.Pressure(new UsageData { Provider = "codex", Session = new UsageWindow() }, Now)
            .Should().Be(((double?)null, false));
        ProviderRefreshPlanner.Pressure(new UsageData
        {
            Provider = "codex", AuthRequired = true, Session = new UsageWindow { ReportedPercent = 99 }
        }, Now).Should().Be(((double?)null, false));
        ProviderRefreshPlanner.Pressure(null, Now).Should().Be(((double?)null, false));
    }

    [TestMethod]
    public void Cadence_ListsActiveProvidersInPlanOrderAndNearLimit()
    {
        var plan = ProviderRefreshPlanner.Plan(
            [Input("claude", activityMinutesAgo: 1), Input("codex", percent: 95, streak: 4), Input("gemini", activityMinutesAgo: 2)],
            Options, Now);

        var cadence = ProviderRefreshPlanner.Cadence(plan);

        cadence.ActiveProviders.Should().Equal("claude", "gemini");
        cadence.NearLimit.Should().BeTrue();
        cadence.Should().Be(new RefreshCadence(["claude", "gemini"], true));
        ProviderRefreshPlanner.Cadence([]).Should().Be(RefreshCadence.None);
    }

    [TestMethod]
    public void Cadence_ProviderInCooldown_IsNotFaster()
    {
        var plan = ProviderRefreshPlanner.Plan(
            [Input("claude", activityMinutesAgo: 1, fetchMinutesAgo: 1, limitedUntil: Now.AddMinutes(9)),
             Input("codex", activityMinutesAgo: 1, fetchMinutesAgo: 1, limitedUntil: Now.AddSeconds(30)),
             Input("gemini", percent: 95, streak: 4, limitedUntil: Now.AddMinutes(1))],
            Options, Now);

        plan[0].Reason.Should().Be(RefreshReason.Active);
        plan[1].RateLimited.Should().BeFalse("the cooldown ends before its active due time");
        plan[2].Reason.Should().Be(RefreshReason.NearLimit);
        ProviderRefreshPlanner.Cadence(plan).IsFaster.Should().BeFalse();
    }

    [TestMethod]
    public void Cadence_ActiveIntervalNoShorterThanQuiet_IsNotFaster()
    {
        // A 1-minute interval: Claude is held to its 2-minute floor either way, and Codex is read every minute either way
        var options = Options with { BaseInterval = TimeSpan.FromMinutes(1) };
        var plan = ProviderRefreshPlanner.Plan(
            [Input("claude", activityMinutesAgo: 1), Input("codex", activityMinutesAgo: 1)], options, Now);

        plan.Should().OnlyContain(p => p.Reason == RefreshReason.Active && !p.Faster);
        plan[0].Interval.Should().Be(TimeSpan.FromMinutes(2));
        ProviderRefreshPlanner.Cadence(plan).IsFaster.Should().BeFalse();
    }

    [TestMethod]
    public void Cadence_ActiveAfterQuietBackoff_IsFaster()
    {
        // Claude backed off to 3 minutes at a 1-minute interval: its 2-minute floor while in use is still faster
        var options = Options with { BaseInterval = TimeSpan.FromMinutes(1) };
        var plan = ProviderRefreshPlanner.Plan([Input("claude", activityMinutesAgo: 1, streak: 4)], options, Now);

        ProviderRefreshPlanner.Cadence(plan).ActiveProviders.Should().Equal("claude");
    }

    [TestMethod]
    public void Cadence_Floor_IsTheShortestMinimumInterval()
    {
        var claudeOnly = ProviderRefreshPlanner.Plan([Input("claude")], Options, Now);
        var both = ProviderRefreshPlanner.Plan([Input("claude"), Input("codex")], Options, Now);

        ProviderRefreshPlanner.Cadence(claudeOnly).Floor.Should().Be(ProviderRefreshPlanner.ClaudeActiveFloor);
        ProviderRefreshPlanner.Cadence(both).Floor.Should().Be(TimeSpan.Zero);
        ProviderRefreshPlanner.Cadence(claudeOnly).Should().NotBe(new RefreshCadence([], false));
    }

    [TestMethod]
    [DataRow(new[] { "claude" }, false, "faster while Claude is in use")]
    [DataRow(new[] { "claude", "codex" }, false, "faster while Claude and Codex are in use")]
    [DataRow(new[] { "claude", "codex", "gemini" }, true, "faster while Claude, Codex and Gemini are in use")]
    [DataRow(new string[0], true, "faster near a limit")]
    public void Caption_PlainUsEnglish(string[] active, bool nearLimit, string expected)
    {
        var caption = RefreshCadence.Caption(active, nearLimit);

        caption.Should().Be(expected);
        caption!.Should().NotContainAny("\u2013", "\u2014");
    }

    [TestMethod]
    public void Caption_NothingFaster_IsNull()
    {
        RefreshCadence.Caption([], false).Should().BeNull();
    }

    [TestMethod]
    public void IsNearLimit_NaNPercent_IsNotNearLimit()
    {
        ProviderRefreshPlanner.IsNearLimit(double.NaN, false).Should().BeFalse();
        ProviderRefreshPlanner.IsNearLimit(null, false).Should().BeFalse();
        ProviderRefreshPlanner.IsNearLimit(null, true).Should().BeTrue();
    }
}
