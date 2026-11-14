namespace ModelHub.Gamification;

/// <summary>A run of consecutive active days.</summary>
/// <param name="Days">How many days long it is; 0 when there is no run.</param>
/// <param name="Start">The first day of the run.</param>
/// <param name="End">The last day of the run.</param>
public readonly record struct StreakRun(int Days, DateOnly? Start, DateOnly? End);

/// <summary>One week of activity, for a row of seven dots.</summary>
/// <param name="Start">The first day of the week.</param>
/// <param name="Days">Seven entries, the first day of the week first; true when that day was active.</param>
/// <param name="DaysActive">How many of the seven days were active.</param>
/// <param name="LongestRun">The longest run of consecutive active days inside the week.</param>
public sealed record StreakWeek(DateOnly Start, IReadOnlyList<bool> Days, int DaysActive, int LongestRun);

/// <summary>
/// Streak arithmetic over the days a user was active. Pass dates in the user's own time zone.
/// </summary>
/// <remarks>
/// To pay a milestone once per streak, fire a <c>StreakMilestone</c> event with <c>streakDays</c> in the context
/// and a reference that includes the run's start, such as <c>$"streak:{run.Start:yyyy-MM-dd}:{run.Days}"</c>.
/// A new streak then pays again, and reprocessing the same day never does.
/// </remarks>
public static class Streaks
{
    /// <summary>
    /// The run of consecutive active days ending today, or yesterday when today has no activity yet,
    /// so a streak isn't shown as broken before the day is over.
    /// </summary>
    /// <param name="activeDays">Days with activity, in any order. Duplicates are ignored.</param>
    /// <param name="today">Today, in the user's time zone.</param>
    public static StreakRun Current(IEnumerable<DateOnly> activeDays, DateOnly today)
    {
        var days = ToSet(activeDays);
        var end = days.Contains(today) ? today : today.AddDays(-1);
        if (!days.Contains(end)) return new StreakRun(0, null, null);

        var start = end;
        while (days.Contains(start.AddDays(-1))) start = start.AddDays(-1);
        return new StreakRun(end.DayNumber - start.DayNumber + 1, start, end);
    }

    /// <summary>The longest run of consecutive active days ever. The earliest wins a tie.</summary>
    /// <param name="activeDays">Days with activity, in any order. Duplicates are ignored.</param>
    public static StreakRun Longest(IEnumerable<DateOnly> activeDays)
    {
        var days = ToSet(activeDays).OrderBy(d => d).ToList();
        var best = new StreakRun(0, null, null);

        var i = 0;
        while (i < days.Count)
        {
            var j = i;
            while (j + 1 < days.Count && days[j + 1].DayNumber == days[j].DayNumber + 1) j++;

            var length = j - i + 1;
            if (length > best.Days) best = new StreakRun(length, days[i], days[j]);
            i = j + 1;
        }

        return best;
    }

    /// <summary>The first day of the week that <paramref name="date"/> falls in.</summary>
    /// <param name="date">Any day.</param>
    /// <param name="firstDayOfWeek">The day weeks start on. Monday by default.</param>
    public static DateOnly WeekStart(DateOnly date, DayOfWeek firstDayOfWeek = DayOfWeek.Monday) =>
        date.AddDays(-(((int)date.DayOfWeek - (int)firstDayOfWeek + 7) % 7));

    /// <summary>The week containing <paramref name="anyDayInWeek"/>: which days were active, and the longest run inside it.</summary>
    /// <param name="activeDays">Days with activity, in any order.</param>
    /// <param name="anyDayInWeek">Any day in the week to show.</param>
    /// <param name="firstDayOfWeek">The day weeks start on. Monday by default.</param>
    public static StreakWeek Week(IEnumerable<DateOnly> activeDays, DateOnly anyDayInWeek, DayOfWeek firstDayOfWeek = DayOfWeek.Monday)
    {
        var days = ToSet(activeDays);
        var start = WeekStart(anyDayInWeek, firstDayOfWeek);

        var flags = new bool[7];
        int run = 0, longest = 0, active = 0;
        for (var i = 0; i < 7; i++)
        {
            flags[i] = days.Contains(start.AddDays(i));
            if (flags[i]) active++;
            run = flags[i] ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return new StreakWeek(start, flags, active, longest);
    }

    private static HashSet<DateOnly> ToSet(IEnumerable<DateOnly> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        return days as HashSet<DateOnly> ?? new HashSet<DateOnly>(days);
    }
}
