namespace ModelHub.Gamification;

/// <summary>
/// Something a user did that may earn points: a lesson finished, a workout logged, an order delivered.
/// Hand it to the points engine after your own save has succeeded.
/// </summary>
/// <param name="TenantId">
/// The customer business the user belongs to. Wallets, rules and rewards never cross tenants.
/// Use <see cref="Guid.Empty"/> in a single-tenant app.
/// </param>
/// <param name="UserId">Your id for the user: an ASP.NET Core Identity id, a GUID, anything up to 128 characters.</param>
/// <param name="EventType">Your name for what happened, such as <c>LessonCompleted</c>. Rules are matched on it. Up to 64 characters.</param>
/// <param name="ReferenceId">
/// The id of the thing that caused the event, such as the lesson attempt id. One event type and reference
/// pays out at most once per user, so retries and duplicate messages never pay twice. Up to 100 characters.
/// </param>
public sealed record GamificationEvent(Guid TenantId, string UserId, string EventType, string ReferenceId)
{
    private static readonly IReadOnlyDictionary<string, object?> NoContext = new Dictionary<string, object?>();

    /// <summary>When it happened, in UTC. Defaults to now. Daily limits and caps count by this time.</summary>
    public DateTime? OccurredAtUtc { get; init; }

    /// <summary>
    /// Facts that rule conditions are matched against, such as <c>score</c> or <c>streakDays</c>.
    /// The engine adds <c>localHour</c> (0–23) and <c>dayOfWeek</c> (e.g. <c>Saturday</c>), in the configured
    /// time zone, unless you supply them yourself.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Context { get; init; } = NoContext;
}

/// <summary>Maximum lengths of the ids the library stores.</summary>
public static class GamificationLimits
{
    /// <summary>Maximum length of a user id.</summary>
    public const int UserIdMaxLength = 128;

    /// <summary>Maximum length of an event type.</summary>
    public const int EventTypeMaxLength = 64;

    /// <summary>Maximum length of a reference id.</summary>
    public const int ReferenceIdMaxLength = 100;

    /// <summary>Maximum length of a redemption code, prefix included.</summary>
    public const int RedemptionCodeMaxLength = 16;

    internal static void ThrowIfInvalidUserId(string? userId, string paramName)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("A user id is required.", paramName);
        if (userId.Length > UserIdMaxLength)
            throw new ArgumentException($"A user id can be at most {UserIdMaxLength} characters.", paramName);
    }

    internal static void ThrowIfInvalidEventType(string? eventType, string paramName)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("An event type is required.", paramName);
        if (eventType.Length > EventTypeMaxLength)
            throw new ArgumentException($"An event type can be at most {EventTypeMaxLength} characters.", paramName);
    }

    internal static void ThrowIfInvalidReferenceId(string? referenceId, string paramName)
    {
        if (string.IsNullOrWhiteSpace(referenceId))
            throw new ArgumentException("A reference id is required: it is what stops the same event paying twice.", paramName);
        if (referenceId.Length > ReferenceIdMaxLength)
            throw new ArgumentException($"A reference id can be at most {ReferenceIdMaxLength} characters.", paramName);
    }
}
