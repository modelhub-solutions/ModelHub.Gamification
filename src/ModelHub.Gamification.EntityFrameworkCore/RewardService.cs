using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ModelHub.Gamification.EntityFrameworkCore;

internal sealed class RewardService<TContext>(TContext db, IOptions<GamificationOptions> options, TimeProvider clock)
    : IRewardService
    where TContext : DbContext
{
    private readonly GamificationOptions _options = options.Value;

    public async Task<IReadOnlyList<Reward>> GetCatalogueAsync(Guid tenantId, bool includeUnavailable = false,
        CancellationToken cancellationToken = default)
    {
        var rewards = db.Set<Reward>().AsNoTracking().Where(r => r.TenantId == tenantId && !r.IsArchived);

        if (!includeUnavailable)
        {
            if (!await RedemptionEnabledAsync(tenantId, cancellationToken)) return [];

            var now = clock.GetUtcNow().UtcDateTime;
            rewards = rewards.Where(r => r.Enabled
                && (r.Stock == null || r.Stock > 0)
                && (r.AvailableFromUtc == null || r.AvailableFromUtc <= now)
                && (r.AvailableUntilUtc == null || r.AvailableUntilUtc > now));
        }

        return await rewards.OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync(cancellationToken);
    }

    public async Task<Reward> SaveRewardAsync(Reward reward, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reward);
        Validate(reward);

        var saved = await db.Set<Reward>().FirstOrDefaultAsync(r => r.Id == reward.Id, cancellationToken);
        if (saved is null)
        {
            saved = reward;
            db.Set<Reward>().Add(saved);
        }
        else if (saved.TenantId != reward.TenantId)
        {
            throw new ArgumentException("A reward with this id belongs to another tenant.", nameof(reward));
        }
        else if (!ReferenceEquals(saved, reward))
        {
            saved.Name = reward.Name;
            saved.Description = reward.Description;
            saved.ImageUrl = reward.ImageUrl;
            saved.Category = reward.Category;
            saved.Cost = reward.Cost;
            saved.Stock = reward.Stock;
            saved.AvailableFromUtc = reward.AvailableFromUtc;
            saved.AvailableUntilUtc = reward.AvailableUntilUtc;
            saved.Fulfilment = reward.Fulfilment;
            saved.Enabled = reward.Enabled;
            saved.IsArchived = reward.IsArchived;
            saved.SortOrder = reward.SortOrder;
        }

        await db.SaveChangesAsync(cancellationToken);
        return saved;
    }

    public async Task<bool> ArchiveRewardAsync(Guid tenantId, Guid rewardId, CancellationToken cancellationToken = default)
    {
        var reward = await db.Set<Reward>().FirstOrDefaultAsync(r => r.Id == rewardId && r.TenantId == tenantId, cancellationToken);
        if (reward is null) return false;
        if (reward.IsArchived) return true;

        reward.IsArchived = true;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<RedeemResult> RedeemAsync(Guid tenantId, string userId, Guid rewardId, CancellationToken cancellationToken = default)
    {
        GamificationLimits.ThrowIfInvalidUserId(userId, nameof(userId));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryRedeemAsync(tenantId, userId, rewardId, cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone else took the last one, or the balance moved. Re-read and decide again.
                db.DetachGamificationEntities();
                if (attempt >= Ledger.MaxAttempts)
                {
                    var balance = await BalanceAsync(tenantId, userId, cancellationToken);
                    return new RedeemResult(RedeemOutcome.TryAgain, null, balance);
                }
            }
            catch (DbUpdateException) when (attempt < Ledger.MaxAttempts)
            {
                db.DetachGamificationEntities(); // a code collision with a redemption saved a moment ago
            }
            catch (DbUpdateException)
            {
                db.DetachGamificationEntities();
                throw;
            }
        }
    }

    private async Task<RedeemResult> TryRedeemAsync(Guid tenantId, string userId, Guid rewardId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var wallet = await Ledger.FindWalletAsync(db, tenantId, userId, ct);
        var balance = wallet?.Balance ?? 0;

        if (!await RedemptionEnabledAsync(tenantId, ct))
            return new RedeemResult(RedeemOutcome.RedemptionDisabled, null, balance);

        var reward = await db.Set<Reward>().FirstOrDefaultAsync(r => r.Id == rewardId && r.TenantId == tenantId, ct);
        if (reward is null || reward.IsArchived || !reward.Enabled
            || reward.AvailableFromUtc > now || reward.AvailableUntilUtc <= now)
            return new RedeemResult(RedeemOutcome.RewardUnavailable, null, balance);
        if (reward.Stock is <= 0)
            return new RedeemResult(RedeemOutcome.OutOfStock, null, balance);
        if (wallet is null || wallet.Balance < reward.Cost)
            return new RedeemResult(RedeemOutcome.NotEnoughPoints, null, balance);

        var instant = reward.Fulfilment == FulfilmentMode.Instant;
        var redemption = new Redemption
        {
            TenantId = tenantId,
            UserId = userId,
            RewardId = reward.Id,
            PointsSpent = reward.Cost,
            Status = instant ? RedemptionStatus.Fulfilled : RedemptionStatus.Pending,
            Code = await RedemptionCodes.NewAsync(db, tenantId, _options, ct),
            CreatedAtUtc = now,
            CompletedAtUtc = instant ? now : null,
        };
        db.Set<Redemption>().Add(redemption);

        if (reward.Stock is { } stock) reward.Stock = stock - 1;
        Ledger.Append(db, wallet, -reward.Cost, LedgerReason.Redeem, now,
            referenceId: redemption.Id.ToString("N"), idempotencyKey: LedgerKeys.Redeem(redemption.Id), note: reward.Name);

        // One save: the points, the stock and the redemption change together or not at all.
        await db.SaveChangesAsync(ct);
        return new RedeemResult(RedeemOutcome.Redeemed, redemption, wallet.Balance);
    }

    public async Task<IReadOnlyList<Redemption>> GetRedemptionsAsync(Guid tenantId, RedemptionStatus? status = null,
        string? userId = null, int take = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(take, 500);

        var query = db.Set<Redemption>().AsNoTracking().Where(r => r.TenantId == tenantId);
        if (status is { } s) query = query.Where(r => r.Status == s);
        if (userId is not null) query = query.Where(r => r.UserId == userId);

        return await query.OrderByDescending(r => r.CreatedAtUtc).Take(take).ToListAsync(cancellationToken);
    }

    public Task<RedemptionUpdateOutcome> FulfilAsync(Guid tenantId, Guid redemptionId, string actor, string? notes = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfInvalid(actor, notes);
        return CompleteAsync(tenantId, redemptionId, RedemptionStatus.Fulfilled, actor, notes, cancellationToken);
    }

    public Task<RedemptionUpdateOutcome> CancelAsync(Guid tenantId, Guid redemptionId, string actor, string? notes = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfInvalid(actor, notes);
        return CompleteAsync(tenantId, redemptionId, RedemptionStatus.Cancelled, actor, notes, cancellationToken);
    }

    public async Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - _options.PendingRedemptionLifetime;
        var due = await db.Set<Redemption>().AsNoTracking()
            .Where(r => r.Status == RedemptionStatus.Pending && r.CreatedAtUtc < cutoff)
            .OrderBy(r => r.CreatedAtUtc)
            .Select(r => new { r.TenantId, r.Id })
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var r in due)
        {
            var outcome = await CompleteAsync(r.TenantId, r.Id, RedemptionStatus.Expired, null, null, cancellationToken);
            if (outcome == RedemptionUpdateOutcome.Updated) expired++;
        }
        return expired;
    }

    /// <summary>
    /// Moves a pending redemption on. Cancel and expiry refund the points and restore stock in the same save;
    /// the status token means only one of fulfil, cancel and expire can win.
    /// </summary>
    private Task<RedemptionUpdateOutcome> CompleteAsync(Guid tenantId, Guid redemptionId, RedemptionStatus status,
        string? actor, string? notes, CancellationToken ct) => db.WithRetryAsync(async () =>
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var redemption = await db.Set<Redemption>().FirstOrDefaultAsync(r => r.Id == redemptionId && r.TenantId == tenantId, ct);
        if (redemption is null) return RedemptionUpdateOutcome.NotFound;
        if (redemption.Status != RedemptionStatus.Pending) return RedemptionUpdateOutcome.NotPending;

        redemption.Status = status;
        redemption.CompletedAtUtc = now;
        redemption.CompletedBy = actor;
        if (notes is not null) redemption.Notes = notes;

        if (status is RedemptionStatus.Cancelled or RedemptionStatus.Expired)
        {
            var reward = await db.Set<Reward>().FirstOrDefaultAsync(r => r.Id == redemption.RewardId, ct);
            if (reward?.Stock is { } stock) reward.Stock = stock + 1;

            var wallet = await Ledger.FindWalletAsync(db, tenantId, redemption.UserId, ct)
                ?? Ledger.AddWallet(db, tenantId, redemption.UserId, now);
            Ledger.Append(db, wallet, redemption.PointsSpent, LedgerReason.Refund, now,
                referenceId: redemption.Id.ToString("N"), idempotencyKey: LedgerKeys.Refund(redemption.Id),
                note: status == RedemptionStatus.Expired ? "Not collected in time" : "Cancelled", createdBy: actor);
        }

        await db.SaveChangesAsync(ct);
        return RedemptionUpdateOutcome.Updated;
    });

    private async Task<bool> RedemptionEnabledAsync(Guid tenantId, CancellationToken ct) =>
        await db.Set<PointsProgram>().AsNoTracking()
            .Where(p => p.TenantId == tenantId)
            .Select(p => (bool?)p.RedemptionEnabled)
            .FirstOrDefaultAsync(ct) ?? true;

    private async Task<long> BalanceAsync(Guid tenantId, string userId, CancellationToken ct) =>
        await db.Set<PointsWallet>().AsNoTracking()
            .Where(w => w.TenantId == tenantId && w.UserId == userId)
            .Select(w => w.Balance)
            .FirstOrDefaultAsync(ct);

    private static void Validate(Reward reward)
    {
        if (string.IsNullOrWhiteSpace(reward.Name)) throw new ArgumentException("A reward needs a name.", nameof(reward));
        if (reward.Name.Length > 120) throw new ArgumentException("A reward name can be at most 120 characters.", nameof(reward));
        if (reward.Description is { Length: > 1000 }) throw new ArgumentException("A description can be at most 1000 characters.", nameof(reward));
        if (reward.ImageUrl is { Length: > 500 }) throw new ArgumentException("An image URL can be at most 500 characters.", nameof(reward));
        if (reward.Category is { Length: > 50 }) throw new ArgumentException("A category can be at most 50 characters.", nameof(reward));
        if (reward.Cost < 1) throw new ArgumentException("A reward must cost at least 1 point.", nameof(reward));
        if (reward.Stock is < 0) throw new ArgumentException("Stock can't be negative.", nameof(reward));
        if (reward.AvailableFromUtc >= reward.AvailableUntilUtc)
            throw new ArgumentException("AvailableFromUtc must be before AvailableUntilUtc.", nameof(reward));
        if (!Enum.IsDefined(reward.Fulfilment)) throw new ArgumentException("Unknown fulfilment mode.", nameof(reward));
    }

    private static void ThrowIfInvalid(string actor, string? notes)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Say who: an actor is required.", nameof(actor));
        if (actor.Length > GamificationLimits.UserIdMaxLength)
            throw new ArgumentException($"An actor can be at most {GamificationLimits.UserIdMaxLength} characters.", nameof(actor));
        if (notes is { Length: > 500 }) throw new ArgumentException("Notes can be at most 500 characters.", nameof(notes));
    }
}
