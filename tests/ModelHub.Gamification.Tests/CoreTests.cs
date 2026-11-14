using System.Text.Json;

namespace ModelHub.Gamification.Tests;

public class RuleConditionTests
{
    private static Dictionary<string, object?> Ctx(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void No_condition_always_matches() => Assert.True(RuleConditions.Matches(null, null));

    [Theory]
    [InlineData(85, true)]
    [InlineData(80, true)]
    [InlineData(79, false)]
    public void Comparison_operators_compare_numbers(int score, bool expected) =>
        Assert.Equal(expected, RuleConditions.Matches("""{"score":{"gte":80}}""", Ctx(("score", score))));

    [Fact]
    public void Numbers_can_arrive_as_text_doubles_or_json()
    {
        const string condition = """{"score":{"gte":80}}""";
        Assert.True(RuleConditions.Matches(condition, Ctx(("score", "85"))));
        Assert.True(RuleConditions.Matches(condition, Ctx(("score", 85.5d))));
        Assert.True(RuleConditions.Matches(condition, Ctx(("score", JsonDocument.Parse("85").RootElement))));
    }

    [Fact]
    public void Range_needs_every_operator_to_hold()
    {
        const string condition = """{"localHour":{"gte":5,"lt":7}}""";
        Assert.True(RuleConditions.Matches(condition, Ctx(("localHour", 6))));
        Assert.False(RuleConditions.Matches(condition, Ctx(("localHour", 7))));
        Assert.False(RuleConditions.Matches(condition, Ctx(("localHour", 4))));
    }

    [Fact]
    public void Equality_ignores_case_for_text_and_keys()
    {
        Assert.True(RuleConditions.Matches("""{"dayOfWeek":"saturday"}""", Ctx(("dayOfWeek", "Saturday"))));
        Assert.True(RuleConditions.Matches("""{"StreakDays":7}""", Ctx(("streakDays", 7))));
        Assert.False(RuleConditions.Matches("""{"streakDays":7}""", Ctx(("streakDays", 30))));
    }

    [Fact]
    public void Booleans_and_not_equal()
    {
        Assert.True(RuleConditions.Matches("""{"premium":true}""", Ctx(("premium", true))));
        Assert.False(RuleConditions.Matches("""{"premium":true}""", Ctx(("premium", false))));
        Assert.True(RuleConditions.Matches("""{"plan":{"ne":"free"}}""", Ctx(("plan", "pro"))));
        Assert.False(RuleConditions.Matches("""{"plan":{"ne":"free"}}""", Ctx(("plan", "Free"))));
    }

    [Theory]
    [InlineData("""{"score":{"gte":80}}""")] // key missing from the context
    [InlineData("{")]
    [InlineData("[1,2]")]
    [InlineData("""{"localHour":{"gte":"early"}}""")]
    public void Missing_keys_and_malformed_conditions_never_match(string condition) =>
        Assert.False(RuleConditions.Matches(condition, Ctx(("localHour", 6))));

    [Theory]
    [InlineData("""{"score":{"between":[1,2]}}""", "Unknown operator")]
    [InlineData("""{"score":[1,2]}""", "must be a number")]
    [InlineData("""{"score":{"gt":"x"}}""", "needs a number")]
    [InlineData("42", "JSON object")]
    [InlineData("{", "not valid JSON")]
    public void IsValid_explains_what_is_wrong(string condition, string expected)
    {
        Assert.False(RuleConditions.IsValid(condition, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void IsValid_accepts_good_and_empty_conditions()
    {
        Assert.True(RuleConditions.IsValid("""{"score":{"gte":80},"dayOfWeek":"Saturday","premium":true}""", out var error));
        Assert.Null(error);
        Assert.True(RuleConditions.IsValid(null, out _));
    }
}

public class LevelTableTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 1)]
    [InlineData(100, 2)]
    [InlineData(250, 3)]
    [InlineData(49_999, 9)]
    [InlineData(50_000, 10)]
    [InlineData(74_999, 10)]
    [InlineData(75_000, 11)]
    [InlineData(-5, 1)]
    public void Default_levels(long points, int level) => Assert.Equal(level, LevelTable.Default.LevelFor(points));

    [Fact]
    public void Progress_to_the_next_level()
    {
        var progress = LevelTable.Default.Progress(300);
        Assert.Equal(new LevelProgress(3, 50, 250, 200), progress);
        Assert.Equal(0.2, progress.Fraction, 3);
        Assert.Equal(75_000, LevelTable.Default.PointsForLevel(11));
    }

    [Fact]
    public void Bad_tables_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new LevelTable([10, 20], 10));
        Assert.Throws<ArgumentException>(() => new LevelTable([0, 20, 20], 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LevelTable([0, 20], 0));
    }
}

public class StreakTests
{
    private static readonly DateOnly[] Days =
    [
        new(2026, 11, 10), new(2026, 11, 11), new(2026, 11, 12),
        new(2026, 11, 14), new(2026, 11, 15), new(2026, 11, 16), new(2026, 11, 16),
    ];

    [Fact]
    public void Current_counts_back_from_today()
    {
        var run = Streaks.Current(Days, new DateOnly(2026, 11, 16));
        Assert.Equal(new StreakRun(3, new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 16)), run);
    }

    [Fact]
    public void Current_is_not_broken_until_the_day_is_over()
    {
        Assert.Equal(3, Streaks.Current(Days, new DateOnly(2026, 11, 17)).Days);
        Assert.Equal(0, Streaks.Current(Days, new DateOnly(2026, 11, 18)).Days);
    }

    [Fact]
    public void Longest_prefers_the_earliest_on_a_tie()
    {
        var run = Streaks.Longest(Days);
        Assert.Equal(3, run.Days);
        Assert.Equal(new DateOnly(2026, 11, 10), run.Start);
    }

    [Fact]
    public void Streaks_run_across_week_boundaries()
    {
        // Thirty days in a row, through several Mondays: weekly views must not reset the count.
        var month = Enumerable.Range(0, 30).Select(i => new DateOnly(2026, 10, 18).AddDays(i));
        Assert.Equal(30, Streaks.Current(month, new DateOnly(2026, 11, 16)).Days);
    }

    [Fact]
    public void Week_view()
    {
        var week = Streaks.Week(Days, new DateOnly(2026, 11, 12));
        Assert.Equal(new DateOnly(2026, 11, 9), week.Start);
        Assert.Equal(new[] { false, true, true, true, false, true, true }, week.Days);
        Assert.Equal(5, week.DaysActive);
        Assert.Equal(3, week.LongestRun);

        Assert.Equal(new DateOnly(2026, 11, 22), Streaks.WeekStart(new DateOnly(2026, 11, 22), DayOfWeek.Sunday));
        Assert.Equal(new DateOnly(2026, 11, 16), Streaks.WeekStart(new DateOnly(2026, 11, 22)));
    }
}

public class AchievementTests
{
    [Fact]
    public void Evaluates_against_metrics_ignoring_case()
    {
        AchievementDefinition[] definitions =
        [
            new("first-lesson", "First lesson", "lessons", 1),
            new("ten-lessons", "Ten lessons", "lessons", 10),
            new("week-streak", "Seven days in a row", "bestStreak", 7),
            new("big-spender", "Spend 1,000 points", "pointsSpent", 1_000),
        ];
        var metrics = new Dictionary<string, long> { ["Lessons"] = 4, ["bestStreak"] = 9 };

        var status = Achievements.Evaluate(definitions, metrics);

        Assert.Equal(new[] { true, false, true, false }, status.Select(s => s.Unlocked));
        Assert.Equal(0.4, status[1].Progress, 3);
        Assert.Equal(1, status[2].Progress);
        Assert.Equal(0, status[3].Current);
    }
}

public class OptionsTests
{
    [Fact]
    public void Defaults_are_valid() => new GamificationOptions().Validate();

    [Fact]
    public void Bad_settings_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new GamificationOptions { RedemptionCodeLength = 3 }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new GamificationOptions { RedemptionCodePrefix = "NORTHBRIDGE-", RedemptionCodeLength = 6 }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new GamificationOptions { PendingRedemptionLifetime = TimeSpan.Zero }.Validate());
    }

    [Fact]
    public void Bad_default_rules_are_rejected()
    {
        var zeroPerDay = new GamificationOptions();
        zeroPerDay.DefaultRules.Add(new RuleDefinition("LessonCompleted", 50) { MaxPerDay = 0 });
        Assert.Throws<InvalidOperationException>(zeroPerDay.Validate);

        var badCondition = new GamificationOptions();
        badCondition.DefaultRules.Add(new RuleDefinition("QuizPassed", 20) { Condition = """{"score":{"atLeast":80}}""" });
        var error = Assert.Throws<InvalidOperationException>(badCondition.Validate);
        Assert.Contains("atLeast", error.Message);

        var noEventType = new GamificationOptions();
        noEventType.DefaultRules.Add(new RuleDefinition(" ", 20));
        Assert.Throws<InvalidOperationException>(noEventType.Validate);
    }
}
