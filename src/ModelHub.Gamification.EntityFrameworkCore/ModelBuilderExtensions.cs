using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ModelHub.Gamification.EntityFrameworkCore;

/// <summary>Adds the gamification tables to your EF Core model.</summary>
public static class GamificationModelBuilderExtensions
{
    /// <summary>
    /// Configures the programme, rule, wallet, ledger, reward and redemption tables. Call it from
    /// <c>OnModelCreating</c>, then add a migration.
    /// </summary>
    /// <param name="modelBuilder">Your model builder.</param>
    /// <param name="schema">Optional database schema, such as <c>gamification</c>.</param>
    /// <param name="tablePrefix">Optional prefix for the table names.</param>
    public static ModelBuilder ApplyGamificationModel(this ModelBuilder modelBuilder, string? schema = null, string tablePrefix = "")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        tablePrefix ??= string.Empty;

        modelBuilder.Entity<PointsProgram>(e =>
        {
            e.ToTable(tablePrefix + "PointsPrograms", schema);
            e.HasKey(p => p.TenantId);
            e.Property(p => p.TenantId).ValueGeneratedNever(); // Guid.Empty is a valid tenant in single-tenant apps
        });

        modelBuilder.Entity<PointsRule>(e =>
        {
            e.ToTable(tablePrefix + "PointsRules", schema);
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.EventType).IsRequired().HasMaxLength(GamificationLimits.EventTypeMaxLength);
            e.Property(r => r.Condition).HasMaxLength(1000);
            e.HasIndex(r => new { r.TenantId, r.EventType });
        });

        modelBuilder.Entity<PointsWallet>(e =>
        {
            e.ToTable(tablePrefix + "PointsWallets", schema);
            e.HasKey(w => w.Id);
            e.Property(w => w.Id).ValueGeneratedNever();
            e.Property(w => w.UserId).IsRequired().HasMaxLength(GamificationLimits.UserIdMaxLength);
            e.Property(w => w.Balance).IsConcurrencyToken();
            e.HasIndex(w => new { w.TenantId, w.UserId }).IsUnique();
        });

        modelBuilder.Entity<PointsLedgerEntry>(e =>
        {
            e.ToTable(tablePrefix + "PointsLedger", schema);
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).ValueGeneratedNever();
            e.Property(l => l.Reason).HasConversion<string>().HasMaxLength(16);
            e.Property(l => l.EventType).HasMaxLength(GamificationLimits.EventTypeMaxLength);
            e.Property(l => l.ReferenceId).HasMaxLength(GamificationLimits.ReferenceIdMaxLength);
            e.Property(l => l.IdempotencyKey).HasMaxLength(200);
            e.Property(l => l.Note).HasMaxLength(500);
            e.Property(l => l.CreatedBy).HasMaxLength(GamificationLimits.UserIdMaxLength);
            e.HasOne<PointsWallet>().WithMany().HasForeignKey(l => l.WalletId).OnDelete(DeleteBehavior.Restrict);

            // The database guarantee behind "pays once". Rows without a key (expiry, adjustments) are not
            // constrained: SQL Server gets an IS NOT NULL filter by convention, other providers treat NULLs as distinct.
            e.HasIndex(l => new { l.WalletId, l.IdempotencyKey }).IsUnique();
            e.HasIndex(l => new { l.WalletId, l.Reason, l.OccurredAtUtc });
            e.HasIndex(l => new { l.TenantId, l.OccurredAtUtc });
        });

        modelBuilder.Entity<Reward>(e =>
        {
            e.ToTable(tablePrefix + "Rewards", schema);
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Name).IsRequired().HasMaxLength(120);
            e.Property(r => r.Description).HasMaxLength(1000);
            e.Property(r => r.ImageUrl).HasMaxLength(500);
            e.Property(r => r.Category).HasMaxLength(50);
            e.Property(r => r.Fulfilment).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Stock).IsConcurrencyToken();
            e.HasIndex(r => new { r.TenantId, r.SortOrder });
        });

        modelBuilder.Entity<Redemption>(e =>
        {
            e.ToTable(tablePrefix + "Redemptions", schema);
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.UserId).IsRequired().HasMaxLength(GamificationLimits.UserIdMaxLength);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16).IsConcurrencyToken();
            e.Property(r => r.Code).IsRequired().HasMaxLength(GamificationLimits.RedemptionCodeMaxLength);
            e.Property(r => r.CompletedBy).HasMaxLength(GamificationLimits.UserIdMaxLength);
            e.Property(r => r.Notes).HasMaxLength(500);
            e.HasOne<Reward>().WithMany().HasForeignKey(r => r.RewardId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => new { r.TenantId, r.Code }).IsUnique();
            e.HasIndex(r => new { r.TenantId, r.Status, r.CreatedAtUtc });
            e.HasIndex(r => new { r.TenantId, r.UserId });
        });

        // Databases hand DateTime back without a kind; mark ours as UTC so APIs serialise them with a "Z".
        foreach (var type in GamificationTypes)
        {
            foreach (var property in modelBuilder.Entity(type).Metadata.GetProperties())
            {
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                    property.SetValueConverter(UtcConverter);
            }
        }

        return modelBuilder;
    }

    private static readonly Type[] GamificationTypes =
        [typeof(PointsProgram), typeof(PointsRule), typeof(PointsWallet), typeof(PointsLedgerEntry), typeof(Reward), typeof(Redemption)];

    private static readonly ValueConverter<DateTime, DateTime> UtcConverter = new(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
