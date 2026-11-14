using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelHub.Gamification.EntityFrameworkCore;

namespace ModelHub.Gamification.Tests;

public class RewardTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task Give(string userId, long points, Guid? tenant = null) =>
        await _h.Wallets.AwardAsync(tenant ?? Harness.Northbridge, userId, points, "Welcome", $"welcome:{userId}");

    [Fact]
    public async Task Redeeming_spends_points_takes_stock_and_issues_a_code()
    {
        await Give("learner-1", 500);
        var reward = await _h.AddReward("1:1 tutor session", 200, stock: 2);

        var result = await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", reward.Id);

        Assert.Equal(RedeemOutcome.Redeemed, result.Outcome);
        Assert.Equal(300, result.Balance);
        var redemption = result.Redemption!;
        Assert.Equal(RedemptionStatus.Pending, redemption.Status);
        Assert.Matches("^RW-[A-HJ-NP-Z2-9]{6}$", redemption.Code);
        Assert.Equal(200, redemption.PointsSpent);

        var db = _h.Db;
        Assert.Equal(1, (await db.Set<Reward>().SingleAsync()).Stock);
        var spend = await db.Set<PointsLedgerEntry>().SingleAsync(l => l.Reason == LedgerReason.Redeem);
        Assert.Equal((-200L, "1:1 tutor session"), (spend.Amount, spend.Note));
    }

    [Fact]
    public async Task Not_enough_points_or_stock()
    {
        await Give("learner-1", 150);
        var tutor = await _h.AddReward("Tutor session", 200);
        var sticker = await _h.AddReward("Sticker pack", 50, stock: 1);

        Assert.Equal(new RedeemResult(RedeemOutcome.NotEnoughPoints, null, 150),
            await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", tutor.Id));
        Assert.True((await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", sticker.Id)).Redeemed);
        Assert.Equal(new RedeemResult(RedeemOutcome.OutOfStock, null, 100),
            await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", sticker.Id));
        Assert.Equal(new RedeemResult(RedeemOutcome.NotEnoughPoints, null, 0),
            await _h.Rewards.RedeemAsync(Harness.Northbridge, "nobody", tutor.Id));
    }

    [Fact]
    public async Task Rewards_from_another_tenant_are_unavailable()
    {
        await Give("learner-1", 500);
        var elsewhere = await _h.AddReward("Riverside hoodie", 100, tenant: Harness.Riverside);

        Assert.Equal(RedeemOutcome.RewardUnavailable,
            (await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", elsewhere.Id)).Outcome);
    }

    [Fact]
    public async Task The_catalogue_shows_users_only_what_they_can_redeem()
    {
        var now = _h.Clock.Now.UtcDateTime;
        await _h.AddReward("Available", 100);
        await _h.Rewards.SaveRewardAsync(new Reward { TenantId = Harness.Northbridge, Name = "Disabled", Cost = 100, Enabled = false });
        await _h.AddReward("Sold out", 100, stock: 0);
        await _h.Rewards.SaveRewardAsync(new Reward
        {
            TenantId = Harness.Northbridge, Name = "Next month", Cost = 100, AvailableFromUtc = now.AddDays(14),
        });
        var archived = await _h.AddReward("Archived", 100);
        await _h.Rewards.ArchiveRewardAsync(Harness.Northbridge, archived.Id);

        var forUsers = await _h.Rewards.GetCatalogueAsync(Harness.Northbridge);
        var forAdmins = await _h.Rewards.GetCatalogueAsync(Harness.Northbridge, includeUnavailable: true);

        Assert.Equal(new[] { "Available" }, forUsers.Select(r => r.Name));
        Assert.Equal(4, forAdmins.Count);
        Assert.Equal(RedeemOutcome.RewardUnavailable,
            (await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", archived.Id)).Outcome);
    }

    [Fact]
    public async Task Rewards_are_validated()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _h.AddReward("", 100));
        await Assert.ThrowsAsync<ArgumentException>(() => _h.AddReward("Free lunch", 0));
        await Assert.ThrowsAsync<ArgumentException>(() => _h.AddReward("Negative", 10, stock: -1));
    }

    [Fact]
    public async Task Updating_a_reward_keeps_its_id()
    {
        var reward = await _h.AddReward("Tutor session", 200, stock: 5);
        await _h.Rewards.SaveRewardAsync(new Reward
        {
            Id = reward.Id, TenantId = Harness.Northbridge, Name = "Tutor session (45 min)", Cost = 250, Stock = 3,
        });

        var saved = await _h.Db.Set<Reward>().SingleAsync();
        Assert.Equal(("Tutor session (45 min)", 250, (int?)3), (saved.Name, saved.Cost, saved.Stock));

        await Assert.ThrowsAsync<ArgumentException>(() => _h.Rewards.SaveRewardAsync(new Reward
        {
            Id = reward.Id, TenantId = Harness.Riverside, Name = "Hijack", Cost = 1,
        }));
    }

    [Fact]
    public async Task Instant_rewards_are_fulfilled_straight_away()
    {
        await Give("learner-1", 500);
        var voucher = await _h.AddReward("10% off next course", 100, fulfilment: FulfilmentMode.Instant);

        var result = await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", voucher.Id);

        Assert.Equal(RedemptionStatus.Fulfilled, result.Redemption!.Status);
        Assert.NotNull(result.Redemption.CompletedAtUtc);
        Assert.Equal(0, (await _h.Programs.GetStatsAsync(Harness.Northbridge)).PendingRedemptions);
    }

    [Fact]
    public async Task Staff_fulfil_a_pending_redemption_once()
    {
        await Give("learner-1", 500);
        var reward = await _h.AddReward("Tutor session", 200);
        var redemption = (await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", reward.Id)).Redemption!;

        Assert.Equal(RedemptionUpdateOutcome.Updated,
            await _h.Rewards.FulfilAsync(Harness.Northbridge, redemption.Id, "staff-1", "Booked for Thursday"));
        Assert.Equal(RedemptionUpdateOutcome.NotPending, await _h.Rewards.FulfilAsync(Harness.Northbridge, redemption.Id, "staff-2"));
        Assert.Equal(RedemptionUpdateOutcome.NotPending, await _h.Rewards.CancelAsync(Harness.Northbridge, redemption.Id, "staff-2"));
        Assert.Equal(RedemptionUpdateOutcome.NotFound, await _h.Rewards.FulfilAsync(Harness.Riverside, redemption.Id, "staff-3"));

        var saved = (await _h.Rewards.GetRedemptionsAsync(Harness.Northbridge, RedemptionStatus.Fulfilled)).Single();
        Assert.Equal(("staff-1", "Booked for Thursday"), (saved.CompletedBy, saved.Notes));
        Assert.Equal(300, (await _h.Wallets.GetSummaryAsync(Harness.Northbridge, "learner-1")).Balance);
    }

    [Fact]
    public async Task Cancelling_refunds_once_and_restores_stock()
    {
        await Give("learner-1", 500);
        var reward = await _h.AddReward("Tutor session", 200, stock: 2);
        var redemption = (await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", reward.Id)).Redemption!;

        Assert.Equal(RedemptionUpdateOutcome.Updated, await _h.Rewards.CancelAsync(Harness.Northbridge, redemption.Id, "staff-1"));
        Assert.Equal(RedemptionUpdateOutcome.NotPending, await _h.Rewards.CancelAsync(Harness.Northbridge, redemption.Id, "staff-1"));

        var db = _h.Db;
        Assert.Equal(500, (await db.Set<PointsWallet>().SingleAsync()).Balance);
        Assert.Equal(500, (await db.Set<PointsWallet>().SingleAsync()).LifetimeEarned); // a refund is not new earning
        Assert.Equal(2, (await db.Set<Reward>().SingleAsync()).Stock);
        Assert.Equal(1, await db.Set<PointsLedgerEntry>().CountAsync(l => l.Reason == LedgerReason.Refund));
        Assert.Equal(RedemptionStatus.Cancelled, (await db.Set<Redemption>().SingleAsync()).Status);
    }

    [Fact]
    public async Task Uncollected_redemptions_expire_with_a_refund()
    {
        await Give("learner-1", 500);
        var reward = await _h.AddReward("Tutor session", 200, stock: 1);
        await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", reward.Id);

        _h.Clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, await _h.Rewards.ExpirePendingAsync());

        _h.Clock.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await _h.Rewards.ExpirePendingAsync());
        Assert.Equal(0, await _h.Rewards.ExpirePendingAsync());

        var db = _h.Db;
        Assert.Equal(RedemptionStatus.Expired, (await db.Set<Redemption>().SingleAsync()).Status);
        Assert.Equal(500, (await db.Set<PointsWallet>().SingleAsync()).Balance);
        Assert.Equal(1, (await db.Set<Reward>().SingleAsync()).Stock);
    }

    [Fact]
    public async Task Redemption_can_be_switched_off_per_tenant()
    {
        await Give("learner-1", 500);
        var reward = await _h.AddReward("Tutor session", 200);
        await _h.Programs.SaveSettingsAsync(new PointsProgram { TenantId = Harness.Northbridge, RedemptionEnabled = false });

        Assert.Equal(new RedeemResult(RedeemOutcome.RedemptionDisabled, null, 500),
            await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", reward.Id));
        Assert.Empty(await _h.Rewards.GetCatalogueAsync(Harness.Northbridge));
        Assert.Single(await _h.Rewards.GetCatalogueAsync(Harness.Northbridge, includeUnavailable: true));
    }

    [Fact]
    public async Task Two_learners_racing_for_the_last_one()
    {
        await Give("learner-1", 500);
        await Give("learner-2", 500);
        var reward = await _h.AddReward("Signed textbook", 300, stock: 1);

        // Learner 2's request read the reward while it still showed one left.
        using var slow = _h.NewScope();
        await slow.ServiceProvider.GetRequiredService<TestDb>().Set<Reward>().SingleAsync();

        Assert.True((await _h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", reward.Id)).Redeemed);
        var late = await slow.ServiceProvider.GetRequiredService<IRewardService>().RedeemAsync(Harness.Northbridge, "learner-2", reward.Id);

        Assert.Equal(new RedeemResult(RedeemOutcome.OutOfStock, null, 500), late);
        var db = _h.Db;
        Assert.Equal(0, (await db.Set<Reward>().SingleAsync()).Stock);
        Assert.Equal(1, await db.Set<Redemption>().CountAsync());
        Assert.Equal(500, (await db.Set<PointsWallet>().SingleAsync(w => w.UserId == "learner-2")).Balance);
    }

    [Fact]
    public async Task Codes_use_the_configured_prefix_and_length()
    {
        using var h = new Harness(o =>
        {
            o.RedemptionCodePrefix = "NB-";
            o.RedemptionCodeLength = 8;
        });
        await h.Wallets.AwardAsync(Harness.Northbridge, "learner-1", 100, "Welcome", "w1");
        var reward = await h.AddReward("Sticker pack", 50);

        var code = (await h.Rewards.RedeemAsync(Harness.Northbridge, "learner-1", reward.Id)).Redemption!.Code;
        Assert.Matches("^NB-[A-HJ-NP-Z2-9]{8}$", code);
    }
}
