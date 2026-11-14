using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ModelHub.Gamification.EntityFrameworkCore;

internal sealed class PointsEngine<TContext>(TContext db, IOptions<GamificationOptions> options, TimeProvider clock)
    : IPointsEngine
    where TContext : DbContext
{
    private readonly GamificationOptions _options = options.Value;

    public async Task<EarnResult> HandleAsync(GamificationEvent gameEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameEvent);
        GamificationLimits.ThrowIfInvalidUserId(gameEvent.UserId, nameof(gameEvent));
        GamificationLimits.ThrowIfInvalidEventType(gameEvent.EventType, nameof(gameEvent));
        GamificationLimits.ThrowIfInvalidReferenceId(gameEvent.ReferenceId, nameof(gameEvent));

        var tenantId = gameEvent.TenantId;
        var now = clock.GetUtcNow().UtcDateTime;
        var occurredAt = gameEvent.OccurredAtUtc is { } at ? TimeWindows.ToUtc(at) : now;
        var key = LedgerKeys.Earn(gameEvent.EventType, gameEvent.ReferenceId);

        var program = await db.Set<PointsProgram>().AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken);
        var rules = await ResolveRulesAsync(tenantId, gameEvent.EventType, cancellationToken);
        var context = WithClockFacts(gameEvent.Context, occurredAt);
        var matched = rules.Where(r => r.Points > 0 && RuleConditions.Matches(r.Condition, context)).ToList();

        // A duplicate event, a first-wallet race or a balance changed underneath all fail the save; the next
        // attempt re-reads, and a duplicate then comes back as AlreadyCredited.
        return await db.WithRetryAsync(() =>
            CreditAsync(gameEvent, key, occurredAt, now, program, matched, cancellationToken));
    }

    private async Task<EarnResult> CreditAsync(
        GamificationEvent gameEvent, string key, DateTime occurredAt, DateTime now,
        PointsProgram? program, List<RuleDefinition> matched, CancellationToken ct)
    {
        var wallet = await Ledger.FindWalletAsync(db, gameEvent.TenantId, gameEvent.UserId, ct);
        var balance = wallet?.Balance ?? 0;

        if (wallet is not null && await Ledger.HasKeyAsync(db, wallet.Id, key, ct))
            return new EarnResult(EarnOutcome.AlreadyCredited, 0, balance);
        if (program is { EarningEnabled: false })
            return new EarnResult(EarnOutcome.EarningDisabled, 0, balance);
        if (matched.Count == 0)
            return new EarnResult(EarnOutcome.NoMatchingRule, 0, balance);

        // Each rule's MaxPerDay counts this event type's credits today; the caps then trim the total
        // to what is left, so a user can land exactly on the cap.
        var (dayStart, dayEnd) = TimeWindows.Day(occurredAt, _options.TimeZone);
        long points = 0;
        int? todayCount = null;
        foreach (var rule in matched)
        {
            if (rule.MaxPerDay is { } max)
            {
                todayCount ??= wallet is null ? 0 : await EarnRows(wallet.Id, dayStart, dayEnd)
                    .CountAsync(l => l.EventType == gameEvent.EventType, ct);
                if (todayCount >= max) continue;
            }
            points += rule.Points;
        }

        if (points > 0 && program?.DailyEarnCap is { } dailyCap)
        {
            var earnedToday = wallet is null ? 0 : await EarnRows(wallet.Id, dayStart, dayEnd).SumAsync(l => l.Amount, ct);
            points = Math.Min(points, dailyCap - earnedToday);
        }

        if (points > 0 && program?.WeeklyEarnCap is { } weeklyCap)
        {
            var (weekStart, weekEnd) = TimeWindows.Week(occurredAt, _options.TimeZone, _options.FirstDayOfWeek);
            var earnedThisWeek = wallet is null ? 0 : await EarnRows(wallet.Id, weekStart, weekEnd).SumAsync(l => l.Amount, ct);
            points = Math.Min(points, weeklyCap - earnedThisWeek);
        }

        if (points <= 0)
            return new EarnResult(EarnOutcome.LimitReached, 0, balance);

        wallet ??= Ledger.AddWallet(db, gameEvent.TenantId, gameEvent.UserId, now);
        Ledger.Append(db, wallet, points, LedgerReason.Earn, now, occurredAt,
            eventType: gameEvent.EventType, referenceId: gameEvent.ReferenceId, idempotencyKey: key);
        await db.SaveChangesAsync(ct);
        return new EarnResult(EarnOutcome.Credited, points, wallet.Balance);
    }

    private IQueryable<PointsLedgerEntry> EarnRows(Guid walletId, DateTime from, DateTime to) =>
        db.Set<PointsLedgerEntry>().Where(l =>
            l.WalletId == walletId && l.Reason == LedgerReason.Earn && l.OccurredAtUtc >= from && l.OccurredAtUtc < to);

    /// <summary>The tenant's enabled rules for the event type if it has any rules for it, otherwise the defaults.</summary>
    private async Task<List<RuleDefinition>> ResolveRulesAsync(Guid tenantId, string eventType, CancellationToken ct)
    {
        var own = await db.Set<PointsRule>().AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.EventType == eventType)
            .OrderBy(r => r.SortOrder)
            .ToListAsync(ct);

        if (own.Count > 0)
        {
            return own.Where(r => r.Enabled)
                .Select(r => new RuleDefinition(r.EventType, r.Points) { Condition = r.Condition, MaxPerDay = r.MaxPerDay })
                .ToList();
        }

        return _options.DefaultRules.Where(r => string.Equals(r.EventType, eventType, StringComparison.Ordinal)).ToList();
    }

    private Dictionary<string, object?> WithClockFacts(IReadOnlyDictionary<string, object?>? context, DateTime occurredAt)
    {
        var facts = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (context is not null)
        {
            foreach (var pair in context) facts[pair.Key] = pair.Value;
        }

        var local = TimeWindows.ToLocal(occurredAt, _options.TimeZone);
        facts.TryAdd("localHour", local.Hour);
        facts.TryAdd("dayOfWeek", local.DayOfWeek.ToString());
        return facts;
    }
}
