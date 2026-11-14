// An online learning platform. Each school is a tenant; learners earn points for lessons, quizzes and streaks,
// and spend them on rewards the school funds. This sample has one school, Northbridge Academy, and no sign-in:
// in a real app the tenant and user come from the signed-in user's claims.

using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ModelHub.Gamification;
using ModelHub.Gamification.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<LearningDb>(o => o.UseSqlite("Data Source=learning-sample.db"));
builder.Services.AddGamification<LearningDb>(o =>
{
    // The platform's defaults. Every school earns with these unless it overrides an event type.
    o.DefaultRules.Add(new RuleDefinition("LessonCompleted", 50) { MaxPerDay = 3 });
    o.DefaultRules.Add(new RuleDefinition("QuizPassed", 20) { Condition = """{"score":{"gte":80}}""" });
    o.DefaultRules.Add(new RuleDefinition("StreakMilestone", 150) { Condition = """{"streakDays":7}""" });
    o.TimeZone = School.TimeZone;
});
builder.Services.AddHostedService<NightlyGamificationJob>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
await School.SeedAsync(app.Services);

var tenant = School.Northbridge;

// ---- Learners ---------------------------------------------------------------------------------------------

app.MapPost("/users/{userId}/lessons/{lessonId}/complete", async (
    string userId, string lessonId, LearningDb db, IPointsEngine points, TimeProvider clock) =>
{
    // 1. Your own work, saved first.
    var activity = new Activity { UserId = userId, Kind = "Lesson", ItemId = lessonId, CompletedAtUtc = clock.GetUtcNow().UtcDateTime };
    db.Activities.Add(activity);
    await db.SaveChangesAsync();

    // 2. Points for it. The activity id is the reference, so a retried request never pays twice.
    var earned = await points.HandleAsync(new GamificationEvent(tenant, userId, "LessonCompleted", activity.Id.ToString()));

    // 3. A streak bonus, once per run: the reference names the run's first day, so day 7 of the
    //    same run can't pay again, and a new run can.
    var streak = Streaks.Current(await School.ActiveDaysAsync(db, userId), School.Today(clock));
    var bonus = streak.Days == 7
        ? await points.HandleAsync(new GamificationEvent(tenant, userId, "StreakMilestone", $"streak:{streak.Start:yyyy-MM-dd}:7")
        {
            Context = new Dictionary<string, object?> { ["streakDays"] = streak.Days },
        })
        : null;

    return Results.Ok(new { earned, streakDays = streak.Days, bonus });
});

app.MapPost("/users/{userId}/quizzes/{quizId}/submit", async (
    string userId, string quizId, int score, LearningDb db, IPointsEngine points, TimeProvider clock) =>
{
    var activity = new Activity { UserId = userId, Kind = "Quiz", ItemId = quizId, Score = score, CompletedAtUtc = clock.GetUtcNow().UtcDateTime };
    db.Activities.Add(activity);
    await db.SaveChangesAsync();

    // The rule's condition, {"score":{"gte":80}}, is matched against this context.
    return await points.HandleAsync(new GamificationEvent(tenant, userId, "QuizPassed", activity.Id.ToString())
    {
        Context = new Dictionary<string, object?> { ["score"] = score },
    });
});

app.MapGet("/users/{userId}/points", (string userId, IWalletService wallets) => wallets.GetSummaryAsync(tenant, userId));

app.MapGet("/users/{userId}/history", (string userId, IWalletService wallets) => wallets.GetHistoryAsync(tenant, userId));

app.MapGet("/users/{userId}/achievements", async (string userId, LearningDb db, IWalletService wallets) =>
{
    var lessons = await db.Activities.CountAsync(a => a.UserId == userId && a.Kind == "Lesson");
    var bestStreak = Streaks.Longest(await School.ActiveDaysAsync(db, userId)).Days;
    var lifetime = (await wallets.GetSummaryAsync(tenant, userId)).LifetimeEarned;

    return Achievements.Evaluate(School.Achievements, new Dictionary<string, long>
    {
        ["lessons"] = lessons,
        ["bestStreak"] = bestStreak,
        ["lifetimePoints"] = lifetime,
    });
});

app.MapGet("/rewards", (IRewardService rewards) => rewards.GetCatalogueAsync(tenant));

app.MapPost("/users/{userId}/rewards/{rewardId:guid}/redeem", async (string userId, Guid rewardId, IRewardService rewards) =>
{
    var result = await rewards.RedeemAsync(tenant, userId, rewardId);
    return result.Redeemed
        ? Results.Ok(new { result.Redemption!.Code, result.Redemption.Status, result.Balance })
        : Results.Conflict(new { result.Outcome, result.Balance });
});

// ---- Staff ------------------------------------------------------------------------------------------------

app.MapGet("/admin/redemptions", (RedemptionStatus? status, IRewardService rewards) => rewards.GetRedemptionsAsync(tenant, status));

app.MapPost("/admin/redemptions/{id:guid}/fulfil", async (Guid id, string staff, IRewardService rewards) =>
    Results.Ok(await rewards.FulfilAsync(tenant, id, staff)));

app.MapPost("/admin/redemptions/{id:guid}/cancel", async (Guid id, string staff, IRewardService rewards) =>
    Results.Ok(await rewards.CancelAsync(tenant, id, staff)));

app.MapPost("/admin/users/{userId}/adjust", async (string userId, Adjustment body, string staff, IWalletService wallets) =>
    Results.Ok(new { balance = await wallets.AdjustAsync(tenant, userId, body.Points, body.Note, staff) }));

app.MapGet("/admin/rules", (IProgramService programs) => programs.GetEffectiveRulesAsync(tenant));

app.MapGet("/admin/stats", (IProgramService programs) => programs.GetStatsAsync(tenant));

app.Run();

// ---- The app's own model ----------------------------------------------------------------------------------

public sealed class Activity
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public int? Score { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}

public sealed class LearningDb(DbContextOptions<LearningDb> options) : DbContext(options)
{
    public DbSet<Activity> Activities => Set<Activity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyGamificationModel();
}

public sealed record Adjustment(long Points, string Note);

internal static class School
{
    public static readonly Guid Northbridge = Guid.Parse("3b0d5f7e-9a1c-4c2e-8f60-2d4b7a9e1c05");

    public static readonly TimeZoneInfo TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public static readonly AchievementDefinition[] Achievements =
    [
        new("first-lesson", "First steps", "lessons", 1),
        new("fifty-lessons", "Half century", "lessons", 50),
        new("week-streak", "Seven days in a row", "bestStreak", 7),
        new("level-5", "Level 5", "lifetimePoints", 1_000),
    ];

    public static DateOnly Today(TimeProvider clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, TimeZone));

    public static async Task<IEnumerable<DateOnly>> ActiveDaysAsync(LearningDb db, string userId)
    {
        var times = await db.Activities.Where(a => a.UserId == userId && a.Kind == "Lesson").Select(a => a.CompletedAtUtc).ToListAsync();
        return times.Select(t => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(t, DateTimeKind.Utc), TimeZone)));
    }

    /// <summary>Starts from an empty database on every run, then sets Northbridge Academy up.</summary>
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LearningDb>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync(); // use migrations in a real app

        var programs = scope.ServiceProvider.GetRequiredService<IProgramService>();
        await programs.SaveSettingsAsync(new PointsProgram { TenantId = Northbridge, DailyEarnCap = 300, PointsExpireAfterDays = 365 });

        // Overriding an event type replaces all its defaults, so Northbridge restates the 50-point lesson
        // and adds an early-bird bonus before 7am in its time zone.
        await programs.SaveRuleAsync(new PointsRule { TenantId = Northbridge, EventType = "LessonCompleted", Points = 50, MaxPerDay = 3 });
        await programs.SaveRuleAsync(new PointsRule
        {
            TenantId = Northbridge, EventType = "LessonCompleted", Points = 10, Condition = """{"localHour":{"lt":7}}""", SortOrder = 1,
        });

        var rewards = scope.ServiceProvider.GetRequiredService<IRewardService>();
        await rewards.SaveRewardAsync(new Reward { TenantId = Northbridge, Name = "1:1 tutor session", Cost = 500, Stock = 10, Category = "Tutoring" });
        await rewards.SaveRewardAsync(new Reward { TenantId = Northbridge, Name = "Printed certificate", Cost = 300, Stock = 25, SortOrder = 1 });
        await rewards.SaveRewardAsync(new Reward
        {
            TenantId = Northbridge, Name = "10% off your next course", Cost = 200, Fulfilment = FulfilmentMode.Instant, SortOrder = 2,
        });
    }
}

/// <summary>Expires old points and uncollected rewards once a day.</summary>
internal sealed class NightlyGamificationJob(IServiceScopeFactory scopes, ILogger<NightlyGamificationJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var wallets = await scope.ServiceProvider.GetRequiredService<IWalletService>().ExpireAsync(stoppingToken);
                var redemptions = await scope.ServiceProvider.GetRequiredService<IRewardService>().ExpirePendingAsync(stoppingToken);
                log.LogInformation("Expired points in {Wallets} wallets and {Redemptions} uncollected redemptions", wallets, redemptions);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Nightly gamification job failed; it will run again tomorrow");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
