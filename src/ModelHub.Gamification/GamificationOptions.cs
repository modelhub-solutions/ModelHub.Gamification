namespace ModelHub.Gamification;

/// <summary>Settings for the points engine, wallets and rewards.</summary>
public sealed class GamificationOptions
{
    /// <summary>
    /// Rules every tenant earns with, out of the box. A tenant's own rules for an event type replace these
    /// for that event type only, including when all of them are disabled, which is how a tenant switches
    /// an event off.
    /// </summary>
    public IList<RuleDefinition> DefaultRules { get; } = new List<RuleDefinition>();

    /// <summary>
    /// The time zone that days and weeks follow, for daily limits, caps and the <c>localHour</c> and
    /// <c>dayOfWeek</c> context values. UTC by default.
    /// </summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;

    /// <summary>The day weekly caps reset. Monday by default.</summary>
    public DayOfWeek FirstDayOfWeek { get; set; } = DayOfWeek.Monday;

    /// <summary>Levels by lifetime points. <see cref="LevelTable.Default"/> unless you set your own.</summary>
    public LevelTable Levels { get; set; } = LevelTable.Default;

    /// <summary>Text in front of every redemption code. <c>RW-</c> by default, as in <c>RW-7K3QX9</c>.</summary>
    public string RedemptionCodePrefix { get; set; } = "RW-";

    /// <summary>
    /// Random characters after the prefix. 6 by default. Codes avoid 0/O and 1/I so they can be read out loud.
    /// </summary>
    public int RedemptionCodeLength { get; set; } = 6;

    /// <summary>
    /// How long a redemption can wait to be fulfilled before it expires and the points are refunded. 30 days by default.
    /// </summary>
    public TimeSpan PendingRedemptionLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Throws when a setting or default rule is not valid. Called when the services are first used.</summary>
    public void Validate()
    {
        foreach (var rule in DefaultRules)
        {
            if (rule is null) throw new InvalidOperationException("DefaultRules contains a null rule.");
            if (string.IsNullOrWhiteSpace(rule.EventType) || rule.EventType.Length > GamificationLimits.EventTypeMaxLength)
                throw new InvalidOperationException(
                    $"Every default rule needs an event type of 1 to {GamificationLimits.EventTypeMaxLength} characters.");
            if (rule.Points < 0)
                throw new InvalidOperationException($"The default rule for '{rule.EventType}' has negative points.");
            if (rule.MaxPerDay is < 1)
                throw new InvalidOperationException($"The default rule for '{rule.EventType}' needs MaxPerDay of 1 or more, or none.");
            if (!RuleConditions.IsValid(rule.Condition, out var error))
                throw new InvalidOperationException($"The default rule for '{rule.EventType}' has an invalid condition. {error}");
        }

        if (TimeZone is null) throw new InvalidOperationException("TimeZone is required.");
        if (Levels is null) throw new InvalidOperationException("Levels is required.");
        if (RedemptionCodePrefix is null) throw new InvalidOperationException("RedemptionCodePrefix can be empty but not null.");
        if (RedemptionCodeLength is < 4 or > 12)
            throw new InvalidOperationException("RedemptionCodeLength must be between 4 and 12.");
        if (RedemptionCodePrefix.Length + RedemptionCodeLength > GamificationLimits.RedemptionCodeMaxLength)
            throw new InvalidOperationException(
                $"A redemption code (prefix plus characters) can be at most {GamificationLimits.RedemptionCodeMaxLength} characters.");
        if (PendingRedemptionLifetime <= TimeSpan.Zero)
            throw new InvalidOperationException("PendingRedemptionLifetime must be positive.");
    }
}
