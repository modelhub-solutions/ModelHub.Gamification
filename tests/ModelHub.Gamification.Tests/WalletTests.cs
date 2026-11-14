using Microsoft.EntityFrameworkCore;
using ModelHub.Gamification.EntityFrameworkCore;

namespace ModelHub.Gamification.Tests;

public class WalletTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task A_new_user_has_an_empty_wallet_at_level_1()
    {
        var summary = await _h.Wallets.GetSummaryAsync(Harness.Northbridge, "nobody");
        Assert.Equal(new WalletSummary(0, 0, new LevelProgress(1, 0, 100, 100)), summary);
        Assert.Empty(await _h.Wallets.GetHistoryAsync(Harness.Northbridge, "nobody"));
    }

    [Fact]
    public async Task Awards_pay_once_per_reference()
    {
        var first = await _h.Wallets.AwardAsync(Harness.Northbridge, "learner-1", 500, "Prize", "nov-challenge", "Top of the leaderboard");
        var again = await _h.Wallets.AwardAsync(Harness.Northbridge, "learner-1", 500, "Prize", "nov-challenge");

        Assert.Equal(new EarnResult(EarnOutcome.Credited, 500, 500), first);
        Assert.Equal(new EarnResult(EarnOutcome.AlreadyCredited, 0, 500), again);
    }

    [Fact]
    public async Task Adjustments_need_a_reason_and_cannot_overdraw()
    {
        await _h.Earn("learner-1", "LessonCompleted", "a1");

        Assert.Equal(60, await _h.Wallets.AdjustAsync(Harness.Northbridge, "learner-1", 10, "Missed lesson credit", "staff-1"));
        Assert.Equal(40, await _h.Wallets.AdjustAsync(Harness.Northbridge, "learner-1", -20, "Duplicate account merge", "staff-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _h.Wallets.AdjustAsync(Harness.Northbridge, "learner-1", -41, "Too much", "staff-1"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _h.Wallets.AdjustAsync(Harness.Northbridge, "learner-1", 5, " ", "staff-1"));

        var summary = await _h.Wallets.GetSummaryAsync(Harness.Northbridge, "learner-1");
        Assert.Equal(40, summary.Balance);
        Assert.Equal(60, summary.LifetimeEarned); // removing points never lowers lifetime points

        var adjustment = await _h.Db.Set<PointsLedgerEntry>().FirstAsync(l => l.Amount == -20);
        Assert.Equal(("staff-1", "Duplicate account merge"), (adjustment.CreatedBy, adjustment.Note));
    }

    [Fact]
    public async Task Levels_come_from_lifetime_points_so_spending_never_costs_a_level()
    {
        await _h.Wallets.AwardAsync(Harness.Northbridge, "learner-1", 300, "Prize", "p1");
        var reward = await _h.AddReward("Tutor session", 200);
        await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", reward.Id);

        var summary = await _h.Wallets.GetSummaryAsync(Harness.Northbridge, "learner-1");
        Assert.Equal(100, summary.Balance);
        Assert.Equal(new LevelProgress(3, 50, 250, 200), summary.Level);
    }

    [Fact]
    public async Task History_is_newest_first()
    {
        await _h.Earn("learner-1", "LessonCompleted", "a1");
        _h.Clock.Advance(TimeSpan.FromMinutes(5));
        await _h.Earn("learner-1", "QuizPassed", "q1", new() { ["score"] = 90 });
        _h.Clock.Advance(TimeSpan.FromMinutes(5));
        await _h.Wallets.AdjustAsync(Harness.Northbridge, "learner-1", -5, "Correction", "staff-1");

        var history = await _h.Wallets.GetHistoryAsync(Harness.Northbridge, "learner-1", take: 2);

        Assert.Equal(2, history.Count);
        Assert.Equal((LedgerReason.Adjust, -5L), (history[0].Reason, history[0].Amount));
        Assert.Equal(("QuizPassed", "q1"), (history[1].EventType, history[1].ReferenceId));
    }

    [Fact]
    public async Task Times_come_back_as_utc()
    {
        await _h.Earn("learner-1", "LessonCompleted", "a1");

        var entry = (await _h.Wallets.GetHistoryAsync(Harness.Northbridge, "learner-1")).Single();
        Assert.Equal(DateTimeKind.Utc, entry.AtUtc.Kind);
        Assert.Equal(_h.Clock.Now.UtcDateTime, entry.AtUtc);

        var wallet = await _h.Db.Set<PointsWallet>().SingleAsync();
        Assert.Equal(DateTimeKind.Utc, wallet.CreatedAtUtc.Kind);
    }

    [Fact]
    public async Task Unspent_points_expire_oldest_first()
    {
        await _h.Programs.SaveSettingsAsync(new PointsProgram { TenantId = Harness.Northbridge, PointsExpireAfterDays = 30 });

        await _h.Earn("learner-1", "LessonCompleted", "a1");                 // day 0: +50
        await _h.Earn("learner-1", "LessonCompleted", "a2");                 // day 0: +50
        _h.Clock.Advance(TimeSpan.FromDays(10));
        await _h.Wallets.AdjustAsync(Harness.Northbridge, "learner-1", -30, "Correction", "staff-1"); // day 10: -30
        _h.Clock.Advance(TimeSpan.FromDays(10));
        await _h.Earn("learner-1", "LessonCompleted", "a3");                 // day 20: +50

        _h.Clock.Advance(TimeSpan.FromDays(11));                             // day 31
        Assert.Equal(1, await _h.Wallets.ExpireAsync());
        Assert.Equal(50, (await _h.Wallets.GetSummaryAsync(Harness.Northbridge, "learner-1")).Balance);

        Assert.Equal(0, await _h.Wallets.ExpireAsync());                     // nothing more is due

        _h.Clock.Advance(TimeSpan.FromDays(20));                             // day 51: day 20's points are now old
        Assert.Equal(1, await _h.Wallets.ExpireAsync());
        var summary = await _h.Wallets.GetSummaryAsync(Harness.Northbridge, "learner-1");
        Assert.Equal(0, summary.Balance);
        Assert.Equal(150, summary.LifetimeEarned);
    }

    [Fact]
    public async Task Points_never_expire_without_a_setting()
    {
        await _h.Earn("learner-1", "LessonCompleted", "a1");
        _h.Clock.Advance(TimeSpan.FromDays(400));
        Assert.Equal(0, await _h.Wallets.ExpireAsync());
    }

    [Fact]
    public async Task Stats_add_up()
    {
        await _h.Earn("learner-1", "LessonCompleted", "a1");
        await _h.Earn("learner-2", "LessonCompleted", "a1");
        await _h.Wallets.AwardAsync(Harness.Northbridge, "learner-1", 500, "Prize", "p1");
        await _h.Wallets.AdjustAsync(Harness.Northbridge, "learner-2", 10, "Goodwill", "staff-1");
        await _h.Wallets.AdjustAsync(Harness.Northbridge, "learner-2", -5, "Correction", "staff-1");
        await _h.Earn("learner-3", "LessonCompleted", "a1", tenant: Harness.Riverside);

        var reward = await _h.AddReward("Tutor session", 100);
        await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", reward.Id);
        var cancelled = await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", reward.Id);
        await _h.Rewards.CancelAsync(Harness.Northbridge, cancelled.Redemption!.Id, "staff-1");

        var stats = await _h.Programs.GetStatsAsync(Harness.Northbridge);
        Assert.Equal(new ProgramStats(PointsIssued: 610, PointsRedeemed: 100, ActiveEarnersLast30Days: 2, PendingRedemptions: 1), stats);
    }

    [Fact]
    public async Task Settings_default_until_saved()
    {
        var settings = await _h.Programs.GetSettingsAsync(Harness.Northbridge);
        Assert.True(settings.EarningEnabled && settings.RedemptionEnabled);
        Assert.Null(settings.DailyEarnCap);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _h.Programs.SaveSettingsAsync(new PointsProgram { TenantId = Harness.Northbridge, DailyEarnCap = 0 }));

        await _h.Programs.SaveSettingsAsync(new PointsProgram { TenantId = Harness.Northbridge, DailyEarnCap = 300 });
        await _h.Programs.SaveSettingsAsync(new PointsProgram { TenantId = Harness.Northbridge, DailyEarnCap = 200 });
        Assert.Equal(200, (await _h.Programs.GetSettingsAsync(Harness.Northbridge)).DailyEarnCap);
    }
}
