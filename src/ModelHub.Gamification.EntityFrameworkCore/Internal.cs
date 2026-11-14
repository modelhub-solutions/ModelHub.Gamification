using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace ModelHub.Gamification.EntityFrameworkCore;

/// <summary>Day and week boundaries in the configured time zone, as UTC ranges for queries.</summary>
internal static class TimeWindows
{
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    public static DateTime ToLocal(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(ToUtc(utc), zone);

    public static (DateTime Start, DateTime End) Day(DateTime utc, TimeZoneInfo zone)
    {
        var date = ToLocal(utc, zone).Date;
        return (MidnightToUtc(date, zone), MidnightToUtc(date.AddDays(1), zone));
    }

    public static (DateTime Start, DateTime End) Week(DateTime utc, TimeZoneInfo zone, DayOfWeek firstDayOfWeek)
    {
        var date = ToLocal(utc, zone).Date;
        var start = date.AddDays(-(((int)date.DayOfWeek - (int)firstDayOfWeek + 7) % 7));
        return (MidnightToUtc(start, zone), MidnightToUtc(start.AddDays(7), zone));
    }

    // A few zones skip midnight when clocks change; start the day at the first time that exists.
    private static DateTime MidnightToUtc(DateTime localDate, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(15);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}

/// <summary>Idempotency keys: one earn per event and reference, one spend and one refund per redemption.</summary>
internal static class LedgerKeys
{
    public static string Earn(string eventType, string referenceId) => $"earn:{eventType}:{referenceId}";
    public static string Award(string eventType, string referenceId) => $"award:{eventType}:{referenceId}";
    public static string Redeem(Guid redemptionId) => $"redeem:{redemptionId:N}";
    public static string Refund(Guid redemptionId) => $"refund:{redemptionId:N}";
}

/// <summary>The only code that writes ledger rows, so balances always move with the ledger.</summary>
internal static class Ledger
{
    public static async Task<PointsWallet?> FindWalletAsync(DbContext db, Guid tenantId, string userId, CancellationToken ct) =>
        await db.Set<PointsWallet>().FirstOrDefaultAsync(w => w.TenantId == tenantId && w.UserId == userId, ct);

    public static PointsWallet AddWallet(DbContext db, Guid tenantId, string userId, DateTime nowUtc)
    {
        var wallet = new PointsWallet { TenantId = tenantId, UserId = userId, CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc };
        db.Set<PointsWallet>().Add(wallet);
        return wallet;
    }

    public static async Task<bool> HasKeyAsync(DbContext db, Guid walletId, string key, CancellationToken ct) =>
        await db.Set<PointsLedgerEntry>().AnyAsync(l => l.WalletId == walletId && l.IdempotencyKey == key, ct);

    public static void Append(
        DbContext db, PointsWallet wallet, long amount, LedgerReason reason, DateTime nowUtc,
        DateTime? occurredAtUtc = null, string? eventType = null, string? referenceId = null,
        string? idempotencyKey = null, string? note = null, string? createdBy = null)
    {
        db.Set<PointsLedgerEntry>().Add(new PointsLedgerEntry
        {
            TenantId = wallet.TenantId,
            WalletId = wallet.Id,
            Amount = amount,
            Reason = reason,
            EventType = eventType,
            ReferenceId = referenceId,
            IdempotencyKey = idempotencyKey,
            Note = note,
            CreatedBy = createdBy,
            OccurredAtUtc = occurredAtUtc ?? nowUtc,
            CreatedAtUtc = nowUtc,
        });

        wallet.Balance += amount;
        if (IsEarning(reason, amount)) wallet.LifetimeEarned += amount;
        wallet.UpdatedAtUtc = nowUtc;
    }

    /// <summary>Earned points count toward lifetime points and levels; refunds don't, they were earned once already.</summary>
    public static bool IsEarning(LedgerReason reason, long amount) =>
        reason is LedgerReason.Earn or LedgerReason.Award || (reason == LedgerReason.Adjust && amount > 0);

    public const int MaxAttempts = 3;

    /// <summary>
    /// Runs a read-decide-save attempt, and on a failed save starts again from the database. The unique
    /// ledger key turns a lost race into "already done"; the balance and stock tokens turn it into a fresh read.
    /// </summary>
    public static async Task<T> WithRetryAsync<T>(this DbContext db, Func<Task<T>> attempt)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                return await attempt();
            }
            catch (DbUpdateException) when (i < MaxAttempts)
            {
                db.DetachGamificationEntities();
            }
            catch (DbUpdateException)
            {
                db.DetachGamificationEntities();
                throw;
            }
        }
    }

    /// <summary>
    /// After a failed save, forget this library's pending changes so the next attempt starts from the database.
    /// Entities of other types are left alone.
    /// </summary>
    public static void DetachGamificationEntities(this DbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is PointsWallet or PointsLedgerEntry or Reward or Redemption or PointsProgram or PointsRule)
                entry.State = EntityState.Detached;
        }
    }
}

internal static class RedemptionCodes
{
    // No 0/O or 1/I: people read the code out loud.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static async Task<string> NewAsync(DbContext db, Guid tenantId, GamificationOptions options, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var chars = new char[options.RedemptionCodeLength];
            for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            var code = options.RedemptionCodePrefix + new string(chars);

            var taken = await db.Set<Redemption>().AnyAsync(r => r.TenantId == tenantId && r.Code == code, ct);
            if (!taken) return code;
        }
        throw new InvalidOperationException("Could not generate a unique redemption code. Consider a longer RedemptionCodeLength.");
    }
}
