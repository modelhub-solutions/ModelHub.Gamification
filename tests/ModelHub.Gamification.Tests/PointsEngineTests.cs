using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelHub.Gamification.EntityFrameworkCore;

namespace ModelHub.Gamification.Tests;

public class PointsEngineTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private static Dictionary<string, object?> Ctx(string key, object? value) => new() { [key] = value };

    [Fact]
    public async Task Default_rule_credits_points()
    {
        var result = await _h.Earn("learner-1", "LessonCompleted", "attempt-1");

        Assert.Equal(new EarnResult(EarnOutcome.Credited, 50, 50), result);
        var summary = await _h.Wallets.GetSummaryAsync(Harness.Northbridge, "learner-1");
        Assert.Equal(50, summary.Balance);
        Assert.Equal(50, summary.LifetimeEarned);
    }

    [Fact]
    public async Task The_same_reference_pays_once()
    {
        await _h.Earn("learner-1", "LessonCompleted", "attempt-1");
        var again = await _h.Earn("learner-1", "LessonCompleted", "attempt-1");

        Assert.Equal(EarnOutcome.AlreadyCredited, again.Outcome);
        Assert.Equal(50, again.Balance);
        Assert.Equal(1, await _h.Db.Set<PointsLedgerEntry>().CountAsync());
    }

    [Fact]
    public async Task Unknown_event_types_earn_nothing()
    {
        var result = await _h.Earn("learner-1", "VideoWatched", "video-1");
        Assert.Equal(new EarnResult(EarnOutcome.NoMatchingRule, 0, 0), result);
        Assert.Equal(0, await _h.Db.Set<PointsWallet>().CountAsync());
    }

    [Fact]
    public async Task MaxPerDay_stops_the_fourth_lesson_and_resets_the_next_day()
    {
        for (var i = 1; i <= 3; i++)
            Assert.True((await _h.Earn("learner-1", "LessonCompleted", $"a{i}")).Credited);

        Assert.Equal(EarnOutcome.LimitReached, (await _h.Earn("learner-1", "LessonCompleted", "a4")).Outcome);

        // An event reported late counts toward the day it happened.
        var yesterday = _h.Clock.Now.UtcDateTime.AddDays(-1);
        Assert.True((await _h.Earn("learner-1", "LessonCompleted", "late", occurredAtUtc: yesterday)).Credited);

        _h.Clock.Advance(TimeSpan.FromDays(1));
        var tomorrow = await _h.Earn("learner-1", "LessonCompleted", "a4");
        Assert.Equal(new EarnResult(EarnOutcome.Credited, 50, 250), tomorrow);
    }

    [Fact]
    public async Task Conditions_are_matched_against_the_event_context()
    {
        Assert.Equal(20, (await _h.Earn("learner-1", "QuizPassed", "quiz-1", Ctx("score", 85))).Points);
        Assert.Equal(EarnOutcome.NoMatchingRule, (await _h.Earn("learner-1", "QuizPassed", "quiz-2", Ctx("score", 70))).Outcome);
        Assert.Equal(EarnOutcome.NoMatchingRule, (await _h.Earn("learner-1", "QuizPassed", "quiz-3")).Outcome);
        Assert.Equal(150, (await _h.Earn("learner-1", "StreakMilestone", "streak:2026-11-10:7", Ctx("streakDays", 7))).Points);
    }

    [Fact]
    public async Task A_tenants_rules_replace_the_defaults_for_that_event_type_only()
    {
        var programs = _h.Programs;
        await programs.SaveRuleAsync(new PointsRule { TenantId = Harness.Northbridge, EventType = "LessonCompleted", Points = 75 });
        var quizOff = await programs.SaveRuleAsync(new PointsRule
        {
            TenantId = Harness.Northbridge, EventType = "QuizPassed", Points = 20, Enabled = false,
        });

        Assert.Equal(75, (await _h.Earn("learner-1", "LessonCompleted", "a1")).Points);
        Assert.Equal(50, (await _h.Earn("learner-2", "LessonCompleted", "a1", tenant: Harness.Riverside)).Points);
        Assert.Equal(EarnOutcome.NoMatchingRule, (await _h.Earn("learner-1", "QuizPassed", "q1", Ctx("score", 90))).Outcome);

        var effective = await _h.Programs.GetEffectiveRulesAsync(Harness.Northbridge);
        Assert.Equal(3, effective.Count);
        Assert.Contains(effective, r => r is { EventType: "LessonCompleted", Points: 75, IsDefault: false });
        Assert.Contains(effective, r => r is { EventType: "QuizPassed", Enabled: false, IsDefault: false });
        Assert.Contains(effective, r => r is { EventType: "StreakMilestone", IsDefault: true });

        // Deleting the tenant's only quiz rule brings the default back.
        Assert.True(await _h.Programs.DeleteRuleAsync(Harness.Northbridge, quizOff.Id));
        Assert.Equal(20, (await _h.Earn("learner-1", "QuizPassed", "q2", Ctx("score", 90))).Points);
    }

    [Fact]
    public async Task Rules_with_bad_conditions_are_refused()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => _h.Programs.SaveRuleAsync(new PointsRule
        {
            TenantId = Harness.Northbridge, EventType = "QuizPassed", Points = 20, Condition = """{"score":{"atLeast":80}}""",
        }));
        Assert.Contains("atLeast", error.Message);
    }

    [Fact]
    public async Task Days_follow_the_configured_time_zone()
    {
        var plusFive = TimeZoneInfo.CreateCustomTimeZone("Test+05", TimeSpan.FromHours(5), "Test+05", "Test+05");
        using var h = new Harness(o =>
        {
            o.TimeZone = plusFive;
            o.DefaultRules.Add(new RuleDefinition("LessonCompleted", 50) { MaxPerDay = 1 });
            o.DefaultRules.Add(new RuleDefinition("LessonCompleted", 10) { Condition = """{"localHour":{"lt":7}}""" });
        });

        h.Clock.Now = new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.Zero); // 06:00 local: early-bird bonus
        Assert.Equal(60, (await h.Earn("learner-1", "LessonCompleted", "a1")).Points);

        h.Clock.Now = new DateTimeOffset(2026, 11, 16, 18, 30, 0, TimeSpan.Zero); // 23:30 local, same day
        Assert.Equal(EarnOutcome.LimitReached, (await h.Earn("learner-1", "LessonCompleted", "a2")).Outcome);

        h.Clock.Now = new DateTimeOffset(2026, 11, 16, 19, 30, 0, TimeSpan.Zero); // 00:30 local, the next day
        Assert.Equal(60, (await h.Earn("learner-1", "LessonCompleted", "a3")).Points);

        h.Clock.Now = new DateTimeOffset(2026, 11, 17, 1, 30, 0, TimeSpan.Zero); // 06:30 local: bonus only, lesson limit used
        Assert.Equal(10, (await h.Earn("learner-1", "LessonCompleted", "a4")).Points);

        h.Clock.Now = new DateTimeOffset(2026, 11, 17, 3, 0, 0, TimeSpan.Zero); // 08:00 local: nothing left today
        Assert.Equal(EarnOutcome.LimitReached, (await h.Earn("learner-1", "LessonCompleted", "a5")).Outcome);
    }

    [Fact]
    public async Task Daily_cap_trims_to_what_is_left_then_stops()
    {
        await _h.Programs.SaveSettingsAsync(new PointsProgram { TenantId = Harness.Northbridge, DailyEarnCap = 80 });

        Assert.Equal(50, (await _h.Earn("learner-1", "LessonCompleted", "a1")).Points);
        Assert.Equal(30, (await _h.Earn("learner-1", "LessonCompleted", "a2")).Points);
        Assert.Equal(EarnOutcome.LimitReached, (await _h.Earn("learner-1", "LessonCompleted", "a3")).Outcome);

        // Awards are outside the caps.
        Assert.True((await _h.Wallets.AwardAsync(Harness.Northbridge, "learner-1", 500, "Prize", "nov-challenge")).Credited);
        Assert.Equal(580, (await _h.Wallets.GetSummaryAsync(Harness.Northbridge, "learner-1")).Balance);
    }

    [Fact]
    public async Task Weekly_cap_resets_on_the_first_day_of_the_week()
    {
        await _h.Programs.SaveSettingsAsync(new PointsProgram { TenantId = Harness.Northbridge, WeeklyEarnCap = 120 });

        Assert.Equal(50, (await _h.Earn("learner-1", "LessonCompleted", "mon")).Points);
        _h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(50, (await _h.Earn("learner-1", "LessonCompleted", "tue")).Points);
        _h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(20, (await _h.Earn("learner-1", "LessonCompleted", "wed")).Points);
        _h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(EarnOutcome.LimitReached, (await _h.Earn("learner-1", "LessonCompleted", "thu")).Outcome);

        _h.Clock.Advance(TimeSpan.FromDays(4)); // Monday again
        Assert.Equal(50, (await _h.Earn("learner-1", "LessonCompleted", "next-mon")).Points);
    }

    [Fact]
    public async Task Earning_can_be_switched_off_per_tenant()
    {
        await _h.Earn("learner-1", "LessonCompleted", "a1");
        await _h.Programs.SaveSettingsAsync(new PointsProgram { TenantId = Harness.Northbridge, EarningEnabled = false });

        Assert.Equal(new EarnResult(EarnOutcome.EarningDisabled, 0, 50), await _h.Earn("learner-1", "LessonCompleted", "a2"));
        Assert.True((await _h.Earn("learner-2", "LessonCompleted", "a1", tenant: Harness.Riverside)).Credited);
    }

    [Fact]
    public async Task Wallets_never_cross_tenants()
    {
        await _h.Earn("learner-1", "LessonCompleted", "a1");
        await _h.Earn("learner-1", "LessonCompleted", "a1", tenant: Harness.Riverside);

        Assert.Equal(50, (await _h.Wallets.GetSummaryAsync(Harness.Northbridge, "learner-1")).Balance);
        Assert.Equal(50, (await _h.Wallets.GetSummaryAsync(Harness.Riverside, "learner-1")).Balance);
        Assert.Equal(2, await _h.Db.Set<PointsWallet>().CountAsync());
    }

    [Fact]
    public async Task The_database_enforces_one_credit_per_key()
    {
        var db = _h.Db;
        var wallet = new PointsWallet { TenantId = Harness.Northbridge, UserId = "learner-1" };
        db.Add(wallet);
        db.Add(new PointsLedgerEntry { TenantId = Harness.Northbridge, WalletId = wallet.Id, Amount = 50, IdempotencyKey = "earn:LessonCompleted:a1" });
        db.Add(new PointsLedgerEntry { TenantId = Harness.Northbridge, WalletId = wallet.Id, Amount = 50, IdempotencyKey = "earn:LessonCompleted:a1" });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Rows_without_a_key_are_not_constrained()
    {
        var db = _h.Db;
        var wallet = new PointsWallet { TenantId = Harness.Northbridge, UserId = "learner-1" };
        db.Add(wallet);
        db.Add(new PointsLedgerEntry { TenantId = Harness.Northbridge, WalletId = wallet.Id, Amount = -5, Reason = LedgerReason.Expire });
        db.Add(new PointsLedgerEntry { TenantId = Harness.Northbridge, WalletId = wallet.Id, Amount = -5, Reason = LedgerReason.Expire });

        Assert.Equal(3, await db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_stale_balance_is_retried_not_overwritten_and_the_apps_changes_are_kept()
    {
        await _h.Earn("learner-1", "LessonCompleted", "a1");

        // A second request loaded the wallet before the next credit landed.
        using var stale = _h.NewScope();
        var staleDb = stale.ServiceProvider.GetRequiredService<TestDb>();
        await staleDb.Set<PointsWallet>().SingleAsync();

        await _h.Earn("learner-1", "LessonCompleted", "a2");

        staleDb.Notes.Add(new Note { Text = "saved alongside the credit" });
        var result = await stale.ServiceProvider.GetRequiredService<IPointsEngine>()
            .HandleAsync(new GamificationEvent(Harness.Northbridge, "learner-1", "QuizPassed", "q1")
            {
                Context = Ctx("score", 95),
            });

        Assert.Equal(new EarnResult(EarnOutcome.Credited, 20, 120), result);
        var db = _h.Db;
        Assert.Equal(120, (await db.Set<PointsWallet>().SingleAsync()).Balance);
        Assert.Equal(120, await db.Set<PointsLedgerEntry>().SumAsync(l => l.Amount));
        Assert.Equal(1, await db.Notes.CountAsync());
    }

    [Fact]
    public async Task Invalid_options_fail_when_the_services_are_first_used()
    {
        using var h = new Harness(o => o.DefaultRules.Add(new RuleDefinition("QuizPassed", 20) { Condition = "{oops" }));
        var error = Record.Exception(() => h.Engine);
        Assert.IsType<InvalidOperationException>(error?.GetBaseException());
    }

    [Theory]
    [InlineData("", "LessonCompleted", "a1")]
    [InlineData("learner-1", "", "a1")]
    [InlineData("learner-1", "LessonCompleted", "")]
    public async Task Events_need_a_user_type_and_reference(string userId, string eventType, string referenceId) =>
        await Assert.ThrowsAsync<ArgumentException>(() => _h.Earn(userId, eventType, referenceId));
}
