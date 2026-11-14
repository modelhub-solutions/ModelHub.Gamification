namespace ModelHub.Gamification;

/// <summary>How many points an event type earns, and when.</summary>
/// <param name="EventType">The event type the rule applies to, such as <c>LessonCompleted</c>.</param>
/// <param name="Points">Points credited when the rule applies. Zero or more.</param>
/// <remarks>
/// Several rules can share an event type; every one whose condition matches adds its points.
/// For example, two <c>StreakMilestone</c> rules with <c>{"streakDays":7}</c> and <c>{"streakDays":30}</c>.
/// </remarks>
public sealed record RuleDefinition(string EventType, int Points)
{
    /// <summary>
    /// Optional JSON condition matched against the event's context, such as <c>{"score":{"gte":80}}</c>.
    /// See <see cref="RuleConditions"/>.
    /// </summary>
    public string? Condition { get; init; }

    /// <summary>Optional limit on how many times the event type can earn, per user, per day.</summary>
    public int? MaxPerDay { get; init; }
}
