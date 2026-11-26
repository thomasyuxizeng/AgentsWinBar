using AwesomeAssertions;
using AgentsWinBar.Helpers;

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
    public void IsActive_AtTheBoundary_IsInclusive()
    {
        ProviderRefreshPlanner.IsActive(Now - ProviderRefreshPlanner.ActiveWindow, Now).Should().BeTrue();
        ProviderRefreshPlanner.IsActive(Now - ProviderRefreshPlanner.ActiveWindow - TimeSpan.FromTicks(1), Now).Should().BeFalse();
        ProviderRefreshPlanner.IsActive(null, Now).Should().BeFalse();
    }

    [TestMethod]
    public void IsNearLimit_NaNPercent_IsNotNearLimit()
    {
        ProviderRefreshPlanner.IsNearLimit(double.NaN, false).Should().BeFalse();
        ProviderRefreshPlanner.IsNearLimit(null, false).Should().BeFalse();
        ProviderRefreshPlanner.IsNearLimit(null, true).Should().BeTrue();
    }
}
