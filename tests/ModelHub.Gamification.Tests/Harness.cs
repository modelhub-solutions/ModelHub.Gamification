using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelHub.Gamification.EntityFrameworkCore;

namespace ModelHub.Gamification.Tests;

/// <summary>A clock the tests move by hand. Starts on Monday 16 November 2026, 09:00 UTC.</summary>
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>An app's own entity, to show the library leaves the app's pending changes alone.</summary>
public sealed class Note
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class TestDb(DbContextOptions<TestDb> options) : DbContext(options)
{
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyGamificationModel();
}

/// <summary>
/// One in-memory SQLite database and a service provider. Every service property resolves from a new scope,
/// like separate web requests, so each call starts with a fresh DbContext.
/// </summary>
public sealed class Harness : IDisposable
{
    public static readonly Guid Northbridge = Guid.Parse("6f1c2a3e-0000-4000-8000-000000000001");
    public static readonly Guid Riverside = Guid.Parse("6f1c2a3e-0000-4000-8000-000000000002");

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly List<IServiceScope> _scopes = [];

    public Harness(Action<GamificationOptions>? configure = null)
    {
        _connection.Open();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddDbContext<TestDb>(o => o.UseSqlite(_connection));
        services.AddGamification<TestDb>(configure ?? LearningPlatform);
        _provider = services.BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TestDb>().Database.EnsureCreated();
    }

    /// <summary>The README's example: an online learning platform.</summary>
    public static void LearningPlatform(GamificationOptions o)
    {
        o.DefaultRules.Add(new RuleDefinition("LessonCompleted", 50) { MaxPerDay = 3 });
        o.DefaultRules.Add(new RuleDefinition("QuizPassed", 20) { Condition = """{"score":{"gte":80}}""" });
        o.DefaultRules.Add(new RuleDefinition("StreakMilestone", 150) { Condition = """{"streakDays":7}""" });
    }

    public TestClock Clock { get; } = new();

    public IServiceScope NewScope()
    {
        var scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope;
    }

    public T Get<T>() where T : notnull => NewScope().ServiceProvider.GetRequiredService<T>();

    public IPointsEngine Engine => Get<IPointsEngine>();
    public IWalletService Wallets => Get<IWalletService>();
    public IRewardService Rewards => Get<IRewardService>();
    public IProgramService Programs => Get<IProgramService>();
    public TestDb Db => Get<TestDb>();

    public Task<EarnResult> Earn(string userId, string eventType, string referenceId,
        Dictionary<string, object?>? context = null, Guid? tenant = null, DateTime? occurredAtUtc = null) =>
        Engine.HandleAsync(new GamificationEvent(tenant ?? Northbridge, userId, eventType, referenceId)
        {
            Context = context ?? new Dictionary<string, object?>(),
            OccurredAtUtc = occurredAtUtc,
        });

    public Task<Reward> AddReward(string name, int cost, int? stock = null, FulfilmentMode fulfilment = FulfilmentMode.Manual,
        Guid? tenant = null) =>
        Rewards.SaveRewardAsync(new Reward
        {
            TenantId = tenant ?? Northbridge,
            Name = name,
            Cost = cost,
            Stock = stock,
            Fulfilment = fulfilment,
        });

    public void Dispose()
    {
        foreach (var scope in _scopes) scope.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }
}
