using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ModelHub.Gamification.EntityFrameworkCore;

internal sealed class WalletService<TContext>(TContext db, IOptions<GamificationOptions> options, TimeProvider clock)
    : IWalletService
    where TContext : DbContext
{
    private const int NoteMaxLength = 500;

    private readonly GamificationOptions _options = options.Value;

    public async Task<WalletSummary> GetSummaryAsync(Guid tenantId, string userId, CancellationToken cancellationToken = default)
    {
        GamificationLimits.ThrowIfInvalidUserId(userId, nameof(userId));

        var wallet = await db.Set<PointsWallet>().AsNoTracking()
            .Where(w => w.TenantId == tenantId && w.UserId == userId)
            .Select(w => new { w.Balance, w.LifetimeEarned })
            .FirstOrDefaultAsync(cancellationToken);

        var lifetime = wallet?.LifetimeEarned ?? 0;
        return new WalletSummary(wallet?.Balance ?? 0, lifetime, _options.Levels.Progress(lifetime));
    }

    public async Task<IReadOnlyList<LedgerItem>> GetHistoryAsync(
        Guid tenantId, string userId, int take = 50, CancellationToken cancellationToken = default)
    {
        GamificationLimits.ThrowIfInvalidUserId(userId, nameof(userId));
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(take, 500);

        var walletId = await db.Set<PointsWallet>().AsNoTracking()
            .Where(w => w.TenantId == tenantId && w.UserId == userId)
            .Select(w => (Guid?)w.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (walletId is null) return [];

        return await db.Set<PointsLedgerEntry>().AsNoTracking()
            .Where(l => l.WalletId == walletId.Value)
            .OrderByDescending(l => l.CreatedAtUtc)
            .Take(take)
            .Select(l => new LedgerItem(l.CreatedAtUtc, l.Amount, l.Reason, l.EventType, l.ReferenceId, l.Note))
            .ToListAsync(cancellationToken);
    }

    public async Task<EarnResult> AwardAsync(Guid tenantId, string userId, long points, string eventType, string referenceId,
        string? note = null, CancellationToken cancellationToken = default)
    {
        GamificationLimits.ThrowIfInvalidUserId(userId, nameof(userId));
        GamificationLimits.ThrowIfInvalidEventType(eventType, nameof(eventType));
        GamificationLimits.ThrowIfInvalidReferenceId(referenceId, nameof(referenceId));
        ArgumentOutOfRangeException.ThrowIfLessThan(points, 1);
        ThrowIfTooLong(note, nameof(note));

        var key = LedgerKeys.Award(eventType, referenceId);
        return await db.WithRetryAsync(async () =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var wallet = await Ledger.FindWalletAsync(db, tenantId, userId, cancellationToken);
            if (wallet is not null && await Ledger.HasKeyAsync(db, wallet.Id, key, cancellationToken))
                return new EarnResult(EarnOutcome.AlreadyCredited, 0, wallet.Balance);

            wallet ??= Ledger.AddWallet(db, tenantId, userId, now);
            Ledger.Append(db, wallet, points, LedgerReason.Award, now,
                eventType: eventType, referenceId: referenceId, idempotencyKey: key, note: note);
            await db.SaveChangesAsync(cancellationToken);
            return new EarnResult(EarnOutcome.Credited, points, wallet.Balance);
        });
    }

    public async Task<long> AdjustAsync(Guid tenantId, string userId, long points, string note, string actor,
        CancellationToken cancellationToken = default)
    {
        GamificationLimits.ThrowIfInvalidUserId(userId, nameof(userId));
        if (points == 0) throw new ArgumentOutOfRangeException(nameof(points), "An adjustment can't be zero.");
        if (string.IsNullOrWhiteSpace(note)) throw new ArgumentException("Say why: a note is required.", nameof(note));
        ThrowIfTooLong(note, nameof(note));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Say who: an actor is required.", nameof(actor));
        if (actor.Length > GamificationLimits.UserIdMaxLength)
            throw new ArgumentException($"An actor can be at most {GamificationLimits.UserIdMaxLength} characters.", nameof(actor));

        return await db.WithRetryAsync(async () =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var wallet = await Ledger.FindWalletAsync(db, tenantId, userId, cancellationToken);
            var balance = wallet?.Balance ?? 0;
            if (balance + points < 0)
                throw new InvalidOperationException($"The user has {balance} points; removing {-points} would make the balance negative.");

            wallet ??= Ledger.AddWallet(db, tenantId, userId, now);
            Ledger.Append(db, wallet, points, LedgerReason.Adjust, now, note: note, createdBy: actor);
            await db.SaveChangesAsync(cancellationToken);
            return wallet.Balance;
        });
    }

    public async Task<int> ExpireAsync(CancellationToken cancellationToken = default)
    {
        var programs = await db.Set<PointsProgram>().AsNoTracking()
            .Where(p => p.PointsExpireAfterDays != null && p.PointsExpireAfterDays > 0)
            .Select(p => new { p.TenantId, Days = p.PointsExpireAfterDays!.Value })
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var program in programs)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var cutoff = now.AddDays(-program.Days);
            var ledger = db.Set<PointsLedgerEntry>().AsNoTracking().Where(l => l.TenantId == program.TenantId);

            // Spending uses the oldest points first. What has expired by now is every point credited before the
            // cutoff, less everything that has ever left the wallet (spends, expiries, deductions).
            var oldCredits = await ledger.Where(l => l.Amount > 0 && l.CreatedAtUtc < cutoff)
                .GroupBy(l => l.WalletId)
                .Select(g => new { WalletId = g.Key, Sum = g.Sum(l => l.Amount) })
                .ToDictionaryAsync(x => x.WalletId, x => x.Sum, cancellationToken);
            if (oldCredits.Count == 0) continue;

            var debits = await ledger.Where(l => l.Amount < 0)
                .GroupBy(l => l.WalletId)
                .Select(g => new { WalletId = g.Key, Sum = g.Sum(l => l.Amount) })
                .ToDictionaryAsync(x => x.WalletId, x => x.Sum, cancellationToken);

            foreach (var (walletId, credited) in oldCredits)
            {
                var due = credited + debits.GetValueOrDefault(walletId);
                if (due <= 0) continue;

                var wallet = await db.Set<PointsWallet>().FirstOrDefaultAsync(w => w.Id == walletId, cancellationToken);
                if (wallet is null || wallet.Balance <= 0) continue;

                Ledger.Append(db, wallet, -Math.Min(due, wallet.Balance), LedgerReason.Expire, now,
                    note: $"Not spent within {program.Days} days");
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    changed++;
                }
                catch (DbUpdateConcurrencyException)
                {
                    // The user earned or spent at the same moment; the next run picks this wallet up again.
                    db.DetachGamificationEntities();
                }
            }
        }

        return changed;
    }

    private static void ThrowIfTooLong(string? note, string paramName)
    {
        if (note is { Length: > NoteMaxLength })
            throw new ArgumentException($"A note can be at most {NoteMaxLength} characters.", paramName);
    }
}
