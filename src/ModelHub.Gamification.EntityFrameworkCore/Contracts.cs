namespace ModelHub.Gamification.EntityFrameworkCore;

/// <summary>What happened to an event.</summary>
public enum EarnOutcome
{
    /// <summary>Points were credited.</summary>
    Credited,

    /// <summary>This event type and reference already paid this user. Nothing was credited.</summary>
    AlreadyCredited,

    /// <summary>No rule matched, or the rules that matched are worth 0 points.</summary>
    NoMatchingRule,

    /// <summary>A per-day limit or the tenant's daily or weekly cap has been reached.</summary>
    LimitReached,

    /// <summary>The tenant has switched earning off.</summary>
    EarningDisabled,
}

/// <summary>The result of handling an event or making an award.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Points">Points credited by this call; 0 unless <see cref="EarnOutcome.Credited"/>.</param>
/// <param name="Balance">The user's balance afterwards.</param>
public sealed record EarnResult(EarnOutcome Outcome, long Points, long Balance)
{
    /// <summary>True when points were credited.</summary>
    public bool Credited => Outcome == EarnOutcome.Credited;
}

/// <summary>What happened to a redemption attempt.</summary>
public enum RedeemOutcome
{
    /// <summary>Redeemed: points spent and a code issued.</summary>
    Redeemed,

    /// <summary>The user doesn't have enough points.</summary>
    NotEnoughPoints,

    /// <summary>The reward doesn't exist, is archived or disabled, or is outside its availability window.</summary>
    RewardUnavailable,

    /// <summary>None left in stock.</summary>
    OutOfStock,

    /// <summary>The tenant has switched redemption off.</summary>
    RedemptionDisabled,

    /// <summary>The balance or stock kept changing underneath; ask the user to try again.</summary>
    TryAgain,
}

/// <summary>The result of a redemption attempt.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Redemption">The redemption, with its code, when <see cref="RedeemOutcome.Redeemed"/>.</param>
/// <param name="Balance">The user's balance afterwards.</param>
public sealed record RedeemResult(RedeemOutcome Outcome, Redemption? Redemption, long Balance)
{
    /// <summary>True when the reward was redeemed.</summary>
    public bool Redeemed => Outcome == RedeemOutcome.Redeemed;
}

/// <summary>What happened when staff fulfilled or cancelled a redemption.</summary>
public enum RedemptionUpdateOutcome
{
    /// <summary>Done.</summary>
    Updated,

    /// <summary>No such redemption for this tenant.</summary>
    NotFound,

    /// <summary>It has already been fulfilled, cancelled or expired.</summary>
    NotPending,
}

/// <summary>A user's points at a glance.</summary>
/// <param name="Balance">Points available to spend.</param>
/// <param name="LifetimeEarned">All points ever earned.</param>
/// <param name="Level">The level from lifetime points, and progress to the next.</param>
public sealed record WalletSummary(long Balance, long LifetimeEarned, LevelProgress Level);

/// <summary>One row of a user's points history.</summary>
/// <param name="AtUtc">When it was written.</param>
/// <param name="Amount">Points added (positive) or removed (negative).</param>
/// <param name="Reason">Why.</param>
/// <param name="EventType">The event type, for earn rows.</param>
/// <param name="ReferenceId">The event reference or redemption id.</param>
/// <param name="Note">The note, such as a reward name.</param>
public sealed record LedgerItem(DateTime AtUtc, long Amount, LedgerReason Reason, string? EventType, string? ReferenceId, string? Note);

/// <summary>A rule as it applies to a tenant: their own, or a default.</summary>
/// <param name="Id">The tenant rule's id; null for a default rule.</param>
/// <param name="EventType">The event type.</param>
/// <param name="Points">Points it earns.</param>
/// <param name="Condition">Its condition, if any.</param>
/// <param name="MaxPerDay">Its per-day limit, if any.</param>
/// <param name="Enabled">False when the tenant has disabled it.</param>
/// <param name="IsDefault">True when it comes from the defaults, not the tenant.</param>
public sealed record EffectiveRule(Guid? Id, string EventType, int Points, string? Condition, int? MaxPerDay, bool Enabled, bool IsDefault);

/// <summary>A tenant's programme in numbers.</summary>
/// <param name="PointsIssued">Points issued from events, awards and positive adjustments.</param>
/// <param name="PointsRedeemed">Points spent on pending and fulfilled redemptions.</param>
/// <param name="ActiveEarnersLast30Days">Users who earned from an event in the last 30 days.</param>
/// <param name="PendingRedemptions">Redemptions waiting to be fulfilled.</param>
public sealed record ProgramStats(long PointsIssued, long PointsRedeemed, int ActiveEarnersLast30Days, int PendingRedemptions);

/// <summary>Turns events into points.</summary>
public interface IPointsEngine
{
    /// <summary>
    /// Works out which rules apply (the tenant's for that event type, otherwise the defaults), checks conditions,
    /// per-day limits and caps, and credits the total once for this event type and reference.
    /// </summary>
    /// <remarks>
    /// It saves through your DbContext, so call it after your own <c>SaveChangesAsync</c>.
    /// Safe to call again for the same event: the second call returns <see cref="EarnOutcome.AlreadyCredited"/>.
    /// </remarks>
    /// <param name="gameEvent">What the user did.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<EarnResult> HandleAsync(GamificationEvent gameEvent, CancellationToken cancellationToken = default);
}

/// <summary>Balances, history, awards, adjustments and expiry.</summary>
public interface IWalletService
{
    /// <summary>The user's balance, lifetime points and level. Zero for a user who hasn't earned yet.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<WalletSummary> GetSummaryAsync(Guid tenantId, string userId, CancellationToken cancellationToken = default);

    /// <summary>The user's most recent ledger rows, newest first.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="take">How many rows, 1 to 500.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<IReadOnlyList<LedgerItem>> GetHistoryAsync(Guid tenantId, string userId, int take = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Credits a fixed amount without rules, limits or caps, such as a competition prize. It works when earning
    /// is switched off and does not count toward the caps, but still pays once per event type and reference.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="points">Points to credit; more than zero.</param>
    /// <param name="eventType">A label for the award, such as <c>Prize</c>.</param>
    /// <param name="referenceId">What the award is for; it pays once.</param>
    /// <param name="note">Optional note.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<EarnResult> AwardAsync(Guid tenantId, string userId, long points, string eventType, string referenceId,
        string? note = null, CancellationToken cancellationToken = default);

    /// <summary>A manual correction by staff, with a reason. Returns the new balance.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="points">Points to add (positive) or remove (negative); not zero.</param>
    /// <param name="note">Why; required.</param>
    /// <param name="actor">Who made the change; required.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="InvalidOperationException">The change would make the balance negative.</exception>
    Task<long> AdjustAsync(Guid tenantId, string userId, long points, string note, string actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Expires unspent points older than each tenant's <see cref="PointsProgram.PointsExpireAfterDays"/>, oldest
    /// first. Run it daily from a background job. Returns how many wallets changed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<int> ExpireAsync(CancellationToken cancellationToken = default);
}

/// <summary>The reward catalogue and the redeem, fulfil and cancel flow.</summary>
public interface IRewardService
{
    /// <summary>
    /// Rewards for a tenant. Users see only what they can redeem right now, and nothing when redemption is off;
    /// pass <paramref name="includeUnavailable"/> for an admin view.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="includeUnavailable">Include disabled, out-of-stock and out-of-window rewards.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<IReadOnlyList<Reward>> GetCatalogueAsync(Guid tenantId, bool includeUnavailable = false, CancellationToken cancellationToken = default);

    /// <summary>Adds a reward, or updates the one with the same id.</summary>
    /// <param name="reward">The reward.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="ArgumentException">A field is not valid.</exception>
    Task<Reward> SaveRewardAsync(Reward reward, CancellationToken cancellationToken = default);

    /// <summary>Hides a reward from the catalogue. It is kept because redemptions refer to it.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="rewardId">The reward.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<bool> ArchiveRewardAsync(Guid tenantId, Guid rewardId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Spends the user's points on a reward and issues a code, in one save: the points, the stock and the
    /// redemption change together or not at all.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="rewardId">The reward.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<RedeemResult> RedeemAsync(Guid tenantId, string userId, Guid rewardId, CancellationToken cancellationToken = default);

    /// <summary>Redemptions for a tenant, newest first, optionally filtered by status or user.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="status">Only this status.</param>
    /// <param name="userId">Only this user.</param>
    /// <param name="take">How many, 1 to 500.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<IReadOnlyList<Redemption>> GetRedemptionsAsync(Guid tenantId, RedemptionStatus? status = null, string? userId = null,
        int take = 100, CancellationToken cancellationToken = default);

    /// <summary>Marks a pending redemption as handed over.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="redemptionId">The redemption.</param>
    /// <param name="actor">The staff member.</param>
    /// <param name="notes">Optional notes.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<RedemptionUpdateOutcome> FulfilAsync(Guid tenantId, Guid redemptionId, string actor, string? notes = null,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels a pending redemption, refunds the points once and returns the item to stock.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="redemptionId">The redemption.</param>
    /// <param name="actor">The staff member.</param>
    /// <param name="notes">Optional notes.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<RedemptionUpdateOutcome> CancelAsync(Guid tenantId, Guid redemptionId, string actor, string? notes = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Expires redemptions still pending after <see cref="GamificationOptions.PendingRedemptionLifetime"/>, refunding
    /// the points and restoring stock. Run it daily. Returns how many expired.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>A tenant's settings and rules.</summary>
public interface IProgramService
{
    /// <summary>The tenant's settings, or the defaults when they have none saved.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<PointsProgram> GetSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Saves the tenant's settings.</summary>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="ArgumentException">A cap or expiry is less than 1.</exception>
    Task<PointsProgram> SaveSettingsAsync(PointsProgram settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// The rules as they apply to the tenant: their own rules, then the defaults for every event type they
    /// haven't overridden. Use it for an admin screen or a "how to earn" list.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<IReadOnlyList<EffectiveRule>> GetEffectiveRulesAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Adds a tenant rule, or updates the one with the same id.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="ArgumentException">A field is not valid, including a malformed condition.</exception>
    Task<PointsRule> SaveRuleAsync(PointsRule rule, CancellationToken cancellationToken = default);

    /// <summary>Deletes a tenant rule. With no rules left for its event type, the defaults apply again.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ruleId">The rule.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<bool> DeleteRuleAsync(Guid tenantId, Guid ruleId, CancellationToken cancellationToken = default);

    /// <summary>Points issued and redeemed, active earners and pending redemptions.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<ProgramStats> GetStatsAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
