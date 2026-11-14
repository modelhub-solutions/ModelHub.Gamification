namespace ModelHub.Gamification;

/// <summary>A badge that unlocks when a metric reaches a threshold.</summary>
/// <param name="Key">A stable id, such as <c>ten-lessons</c>.</param>
/// <param name="Name">The name to show, such as <c>Ten in a row</c>.</param>
/// <param name="Metric">The metric it tracks, such as <c>lessons</c> or <c>bestStreak</c>.</param>
/// <param name="Threshold">The value of the metric that unlocks it.</param>
public sealed record AchievementDefinition(string Key, string Name, string Metric, long Threshold)
{
    /// <summary>Optional description, such as "Complete 10 lessons".</summary>
    public string? Description { get; init; }

    /// <summary>Optional icon: an emoji, an icon name or an image URL.</summary>
    public string? Icon { get; init; }
}

/// <summary>Where a user stands on one achievement.</summary>
/// <param name="Definition">The achievement.</param>
/// <param name="Current">The user's current value for the metric.</param>
/// <param name="Unlocked">True when <paramref name="Current"/> has reached the threshold.</param>
public sealed record AchievementStatus(AchievementDefinition Definition, long Current, bool Unlocked)
{
    /// <summary>Progress towards the threshold, from 0 to 1.</summary>
    public double Progress => Definition.Threshold <= 0
        ? 1
        : Math.Min(1d, Math.Max(0, Current) / (double)Definition.Threshold);
}

/// <summary>Evaluates achievements against a user's metrics.</summary>
/// <remarks>
/// Use lifetime values (lessons completed, best streak, lifetime points), not balances, so an achievement
/// never locks again after it is earned.
/// </remarks>
public static class Achievements
{
    /// <summary>The status of each achievement, in the order given. A metric that isn't supplied counts as 0.</summary>
    /// <param name="definitions">The achievements to evaluate.</param>
    /// <param name="metrics">The user's metrics by name, matched ignoring case.</param>
    public static IReadOnlyList<AchievementStatus> Evaluate(
        IEnumerable<AchievementDefinition> definitions, IReadOnlyDictionary<string, long> metrics)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(metrics);

        return definitions
            .Select(d =>
            {
                var current = Lookup(metrics, d.Metric);
                return new AchievementStatus(d, current, current >= d.Threshold);
            })
            .ToList();
    }

    private static long Lookup(IReadOnlyDictionary<string, long> metrics, string name)
    {
        if (metrics.TryGetValue(name, out var value)) return value;
        foreach (var pair in metrics)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        }
        return 0;
    }
}
