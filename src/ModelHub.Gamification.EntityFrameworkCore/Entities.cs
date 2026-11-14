namespace ModelHub.Gamification.EntityFrameworkCore;

/// <summary>A tenant's programme settings. No row means the defaults: earning and redemption on, no caps, no expiry.</summary>
public class PointsProgram
{
    /// <summary>The tenant. One programme per tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>When false, no event earns points for this tenant.</summary>
    public bool EarningEnabled { get; set; } = true;

    /// <summary>When false, users can't redeem rewards and the catalogue is hidden from them.</summary>
    public bool RedemptionEnabled { get; set; } = true;

    /// <summary>Most points a user can earn from events in one day. Null for no cap.</summary>
    public long? DailyEarnCap { get; set; }

    /// <summary>Most points a user can earn from events in one week. Null for no cap.</summary>
    public long? WeeklyEarnCap { get; set; }

    /// <summary>Points not spent within this many days of being earned expire. Null means points never expire.</summary>
    public int? PointsExpireAfterDays { get; set; }

    /// <summary>When the settings last changed (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A tenant's rule for one event type. A tenant's rules for an event type replace the default rules for it.</summary>
public class PointsRule
{
    /// <summary>The rule's id.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tenant that owns the rule.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The event type the rule applies to.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Points credited when the rule applies.</summary>
    public int Points { get; set; }

    /// <summary>Optional JSON condition, such as <c>{"score":{"gte":80}}</c>.</summary>
    public string? Condition { get; set; }

    /// <summary>Optional limit on credits for this event type per user per day.</summary>
    public int? MaxPerDay { get; set; }

    /// <summary>Disabled rules earn nothing, but still replace the defaults for their event type.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Display order.</summary>
    public int SortOrder { get; set; }
}

/// <summary>One user's points in one tenant. The ledger is the record; the balance is kept in step with it.</summary>
public class PointsWallet
{
    /// <summary>The wallet's id.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The user.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Points available to spend. A concurrency token, so two spends can't both use the same points.</summary>
    public long Balance { get; set; }

    /// <summary>All points ever earned, including positive adjustments. Spending and expiry never reduce it.</summary>
    public long LifetimeEarned { get; set; }

    /// <summary>When the wallet was created (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the balance last changed (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Why a ledger row exists.</summary>
public enum LedgerReason
{
    /// <summary>Points earned from an event through the rules. Per-day limits and caps count these rows.</summary>
    Earn,

    /// <summary>A fixed award, such as a prize, outside the rules, limits and caps.</summary>
    Award,

    /// <summary>Points spent on a reward.</summary>
    Redeem,

    /// <summary>Points returned after a redemption was cancelled or expired.</summary>
    Refund,

    /// <summary>Points that expired unspent.</summary>
    Expire,

    /// <summary>A manual change by staff, with a note.</summary>
    Adjust,
}

/// <summary>One change to a wallet. Rows are only ever added, never changed or removed.</summary>
public class PointsLedgerEntry
{
    /// <summary>The row's id.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The wallet that changed.</summary>
    public Guid WalletId { get; set; }

    /// <summary>Positive for earn, refund and adjustments up; negative for redeem, expiry and adjustments down.</summary>
    public long Amount { get; set; }

    /// <summary>Why the row exists.</summary>
    public LedgerReason Reason { get; set; }

    /// <summary>The event type, on earn rows.</summary>
    public string? EventType { get; set; }

    /// <summary>The event's reference, or the redemption id for spends and refunds.</summary>
    public string? ReferenceId { get; set; }

    /// <summary>Unique per wallet when set. It is what stops an event, a spend or a refund happening twice.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>A note, such as the reward name or the reason for an adjustment.</summary>
    public string? Note { get; set; }

    /// <summary>Who made a manual change.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>When the event happened (UTC). Daily limits and caps count by this time.</summary>
    public DateTime OccurredAtUtc { get; set; }

    /// <summary>When the row was written (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>How a redeemed reward reaches the user.</summary>
public enum FulfilmentMode
{
    /// <summary>The user shows the code and staff mark it fulfilled. The default.</summary>
    Manual,

    /// <summary>Fulfilled straight away: the code itself is the reward, such as a voucher or an unlock.</summary>
    Instant,
}

/// <summary>An item in a tenant's reward catalogue.</summary>
public class Reward
{
    /// <summary>The reward's id.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tenant that offers it.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The name, up to 120 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>An optional description.</summary>
    public string? Description { get; set; }

    /// <summary>An optional image URL.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>An optional category of your choosing, such as <c>Discount</c> or <c>Merch</c>.</summary>
    public string? Category { get; set; }

    /// <summary>The cost in points. More than zero.</summary>
    public int Cost { get; set; }

    /// <summary>How many are left; null for unlimited. Goes down on redeem and back up on cancel or expiry.</summary>
    public int? Stock { get; set; }

    /// <summary>Optional start of availability (UTC).</summary>
    public DateTime? AvailableFromUtc { get; set; }

    /// <summary>Optional end of availability (UTC).</summary>
    public DateTime? AvailableUntilUtc { get; set; }

    /// <summary>How the reward reaches the user.</summary>
    public FulfilmentMode Fulfilment { get; set; } = FulfilmentMode.Manual;

    /// <summary>Disabled rewards can't be redeemed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Archived rewards are hidden but kept, because redemptions refer to them.</summary>
    public bool IsArchived { get; set; }

    /// <summary>Display order.</summary>
    public int SortOrder { get; set; }
}

/// <summary>Where a redemption is up to.</summary>
public enum RedemptionStatus
{
    /// <summary>Waiting for staff to hand the reward over.</summary>
    Pending,

    /// <summary>Handed over.</summary>
    Fulfilled,

    /// <summary>Cancelled by staff; the points were refunded.</summary>
    Cancelled,

    /// <summary>Not collected in time; the points were refunded.</summary>
    Expired,
}

/// <summary>A user spending points on a reward.</summary>
public class Redemption
{
    /// <summary>The redemption's id.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tenant.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The user who redeemed.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The reward redeemed.</summary>
    public Guid RewardId { get; set; }

    /// <summary>Points spent, fixed at the reward's cost when redeemed.</summary>
    public int PointsSpent { get; set; }

    /// <summary>Where it is up to. A concurrency token, so it can't be fulfilled and cancelled at the same time.</summary>
    public RedemptionStatus Status { get; set; }

    /// <summary>The code the user shows, such as <c>RW-7K3QX9</c>. Unique per tenant.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>When it was redeemed (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When it was fulfilled, cancelled or expired (UTC).</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>The staff member who fulfilled or cancelled it.</summary>
    public string? CompletedBy { get; set; }

    /// <summary>Optional notes.</summary>
    public string? Notes { get; set; }
}
