namespace ModelHub.Gamification;

/// <summary>Turns lifetime points into a level, and shows progress towards the next one.</summary>
/// <remarks>
/// Base levels on lifetime points earned, not the spendable balance, so redeeming a reward never costs a level.
/// </remarks>
public sealed class LevelTable
{
    private readonly long[] _thresholds;

    /// <summary>
    /// Levels 1–10 at 0, 100, 250, 500, 1,000, 2,000, 5,000, 10,000, 25,000 and 50,000 points,
    /// then one more level every 25,000 points.
    /// </summary>
    public static LevelTable Default { get; } =
        new([0, 100, 250, 500, 1_000, 2_000, 5_000, 10_000, 25_000, 50_000], 25_000);

    /// <summary>Creates a level table.</summary>
    /// <param name="thresholds">Points needed for level 1, 2, 3 and so on. The first must be 0, and each must be higher than the one before.</param>
    /// <param name="pointsPerLevelAfterTable">Points for each extra level after the last threshold.</param>
    public LevelTable(IEnumerable<long> thresholds, long pointsPerLevelAfterTable)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        _thresholds = thresholds.ToArray();

        if (_thresholds.Length == 0 || _thresholds[0] != 0)
            throw new ArgumentException("The first threshold must be 0, for level 1.", nameof(thresholds));
        for (var i = 1; i < _thresholds.Length; i++)
        {
            if (_thresholds[i] <= _thresholds[i - 1])
                throw new ArgumentException("Each threshold must be higher than the one before.", nameof(thresholds));
        }
        if (pointsPerLevelAfterTable <= 0)
            throw new ArgumentOutOfRangeException(nameof(pointsPerLevelAfterTable), "Must be more than zero.");

        PointsPerLevelAfterTable = pointsPerLevelAfterTable;
    }

    /// <summary>Points needed for each level in the table, level 1 first.</summary>
    public IReadOnlyList<long> Thresholds => _thresholds;

    /// <summary>Points for each extra level after the last threshold.</summary>
    public long PointsPerLevelAfterTable { get; }

    /// <summary>The level reached with <paramref name="points"/>. Level 1 is the lowest.</summary>
    /// <param name="points">Lifetime points.</param>
    public int LevelFor(long points)
    {
        if (points <= 0) return 1;

        var last = _thresholds.Length - 1;
        if (points >= _thresholds[last])
        {
            var extra = (points - _thresholds[last]) / PointsPerLevelAfterTable;
            return (int)Math.Min(int.MaxValue, last + 1 + extra);
        }

        for (var i = last - 1; i >= 0; i--)
        {
            if (points >= _thresholds[i]) return i + 1;
        }
        return 1;
    }

    /// <summary>Points needed to reach <paramref name="level"/>.</summary>
    /// <param name="level">The level, 1 or more.</param>
    public long PointsForLevel(int level)
    {
        if (level <= 1) return 0;
        if (level <= _thresholds.Length) return _thresholds[level - 1];
        return _thresholds[^1] + (long)(level - _thresholds.Length) * PointsPerLevelAfterTable;
    }

    /// <summary>The level for <paramref name="points"/> and how far it is to the next one.</summary>
    /// <param name="points">Lifetime points.</param>
    public LevelProgress Progress(long points)
    {
        var p = Math.Max(0, points);
        var level = LevelFor(p);
        var start = PointsForLevel(level);
        var next = PointsForLevel(level + 1);
        return new LevelProgress(level, p - start, next - start, next - p);
    }
}

/// <summary>A user's level and progress towards the next one.</summary>
/// <param name="Level">The current level, 1 or more.</param>
/// <param name="PointsIntoLevel">Points earned since reaching this level.</param>
/// <param name="LevelSize">Points between this level and the next.</param>
/// <param name="PointsToNextLevel">Points still needed for the next level.</param>
public readonly record struct LevelProgress(int Level, long PointsIntoLevel, long LevelSize, long PointsToNextLevel)
{
    /// <summary>Progress through the level, from 0 to 1, for a progress bar.</summary>
    public double Fraction => LevelSize <= 0 ? 1 : (double)PointsIntoLevel / LevelSize;
}
