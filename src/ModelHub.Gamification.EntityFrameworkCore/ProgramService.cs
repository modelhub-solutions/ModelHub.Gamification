using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ModelHub.Gamification.EntityFrameworkCore;

internal sealed class ProgramService<TContext>(TContext db, IOptions<GamificationOptions> options, TimeProvider clock)
    : IProgramService
    where TContext : DbContext
{
    private const int ConditionMaxLength = 1000;

    private readonly GamificationOptions _options = options.Value;

    public async Task<PointsProgram> GetSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await db.Set<PointsProgram>().AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken)
        ?? new PointsProgram { TenantId = tenantId };

    public async Task<PointsProgram> SaveSettingsAsync(PointsProgram settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.DailyEarnCap is < 1) throw new ArgumentException("DailyEarnCap must be 1 or more, or null for no cap.", nameof(settings));
        if (settings.WeeklyEarnCap is < 1) throw new ArgumentException("WeeklyEarnCap must be 1 or more, or null for no cap.", nameof(settings));
        if (settings.PointsExpireAfterDays is < 1)
            throw new ArgumentException("PointsExpireAfterDays must be 1 or more, or null for no expiry.", nameof(settings));

        var saved = await db.Set<PointsProgram>().FirstOrDefaultAsync(p => p.TenantId == settings.TenantId, cancellationToken);
        if (saved is null)
        {
            saved = settings;
            db.Set<PointsProgram>().Add(saved);
        }
        else if (!ReferenceEquals(saved, settings))
        {
            saved.EarningEnabled = settings.EarningEnabled;
            saved.RedemptionEnabled = settings.RedemptionEnabled;
            saved.DailyEarnCap = settings.DailyEarnCap;
            saved.WeeklyEarnCap = settings.WeeklyEarnCap;
            saved.PointsExpireAfterDays = settings.PointsExpireAfterDays;
        }

        saved.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return saved;
    }

    public async Task<IReadOnlyList<EffectiveRule>> GetEffectiveRulesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var own = await db.Set<PointsRule>().AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.EventType)
            .ToListAsync(cancellationToken);

        var overridden = own.Select(r => r.EventType).ToHashSet(StringComparer.Ordinal);
        var rules = own
            .Select(r => new EffectiveRule(r.Id, r.EventType, r.Points, r.Condition, r.MaxPerDay, r.Enabled, IsDefault: false))
            .ToList();
        rules.AddRange(_options.DefaultRules
            .Where(r => !overridden.Contains(r.EventType))
            .Select(r => new EffectiveRule(null, r.EventType, r.Points, r.Condition, r.MaxPerDay, Enabled: true, IsDefault: true)));
        return rules;
    }

    public async Task<PointsRule> SaveRuleAsync(PointsRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        GamificationLimits.ThrowIfInvalidEventType(rule.EventType, nameof(rule));
        if (rule.Points < 0) throw new ArgumentException("A rule can't take points away.", nameof(rule));
        if (rule.MaxPerDay is < 1) throw new ArgumentException("MaxPerDay must be 1 or more, or null for no limit.", nameof(rule));
        if (rule.Condition is { Length: > ConditionMaxLength })
            throw new ArgumentException($"A condition can be at most {ConditionMaxLength} characters.", nameof(rule));
        if (!RuleConditions.IsValid(rule.Condition, out var error))
            throw new ArgumentException($"The condition is not valid. {error}", nameof(rule));

        var saved = await db.Set<PointsRule>().FirstOrDefaultAsync(r => r.Id == rule.Id, cancellationToken);
        if (saved is null)
        {
            saved = rule;
            db.Set<PointsRule>().Add(saved);
        }
        else if (saved.TenantId != rule.TenantId)
        {
            throw new ArgumentException("A rule with this id belongs to another tenant.", nameof(rule));
        }
        else if (!ReferenceEquals(saved, rule))
        {
            saved.EventType = rule.EventType;
            saved.Points = rule.Points;
            saved.Condition = rule.Condition;
            saved.MaxPerDay = rule.MaxPerDay;
            saved.Enabled = rule.Enabled;
            saved.SortOrder = rule.SortOrder;
        }

        await db.SaveChangesAsync(cancellationToken);
        return saved;
    }

    public async Task<bool> DeleteRuleAsync(Guid tenantId, Guid ruleId, CancellationToken cancellationToken = default)
    {
        var rule = await db.Set<PointsRule>().FirstOrDefaultAsync(r => r.Id == ruleId && r.TenantId == tenantId, cancellationToken);
        if (rule is null) return false;

        db.Set<PointsRule>().Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ProgramStats> GetStatsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        var ledger = db.Set<PointsLedgerEntry>().AsNoTracking().Where(l => l.TenantId == tenantId);
        var redemptions = db.Set<Redemption>().AsNoTracking().Where(r => r.TenantId == tenantId);

        var issued = await ledger
            .Where(l => l.Reason == LedgerReason.Earn || l.Reason == LedgerReason.Award
                || (l.Reason == LedgerReason.Adjust && l.Amount > 0))
            .SumAsync(l => l.Amount, cancellationToken);

        var redeemed = await redemptions
            .Where(r => r.Status == RedemptionStatus.Pending || r.Status == RedemptionStatus.Fulfilled)
            .SumAsync(r => (long)r.PointsSpent, cancellationToken);

        var activeEarners = await ledger
            .Where(l => l.Reason == LedgerReason.Earn && l.OccurredAtUtc >= since)
            .Select(l => l.WalletId).Distinct()
            .CountAsync(cancellationToken);

        var pending = await redemptions.CountAsync(r => r.Status == RedemptionStatus.Pending, cancellationToken);

        return new ProgramStats(issued, redeemed, activeEarners, pending);
    }
}
