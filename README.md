# ModelHub.Gamification

[![NuGet](https://img.shields.io/nuget/v/ModelHub.Gamification?label=ModelHub.Gamification&logo=nuget)](https://www.nuget.org/packages/ModelHub.Gamification)
[![NuGet](https://img.shields.io/nuget/v/ModelHub.Gamification.EntityFrameworkCore?label=ModelHub.Gamification.EntityFrameworkCore&logo=nuget)](https://www.nuget.org/packages/ModelHub.Gamification.EntityFrameworkCore)
[![CI](https://github.com/modelhub-solutions/ModelHub.Gamification/actions/workflows/ci.yml/badge.svg)](https://github.com/modelhub-solutions/ModelHub.Gamification/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4)](https://dotnet.microsoft.com/)
[![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue)](LICENSE)

A **points and rewards engine for ASP.NET Core and EF Core**.
Your app reports what users do. Rules decide the points, and a ledger records every change.
Users spend their points on rewards that your business funds.

Extracted from the rewards engine in the [StrideWell](https://stridewell.app) API, a multi-tenant SaaS platform for gyms and coaches.

```
dotnet add package ModelHub.Gamification.EntityFrameworkCore
```

Targets .NET 8 (EF Core 8) and .NET 10 (EF Core 10). It works with any EF Core relational provider.

## What you get

- **Rules per event type**, with JSON conditions (`{"score":{"gte":80}}`), per-day limits, and daily and weekly caps. The platform sets the defaults and each tenant can override them.
- **Pays once.** An event type and reference credit a user at most once. A unique index enforces this, so retries, double-clicks and duplicate queue messages never double-pay.
- **A ledger, not just a number.** Every earn, award, spend, refund, expiry and adjustment is a row, and the balance moves with it. Balances use optimistic concurrency, so two spends can't use the same points.
- **Rewards**:
  - a catalogue with stock and availability windows;
  - readable redemption codes (`RW-7K3QX9`);
  - fulfilment by staff, or instant;
  - cancellation that refunds the points exactly once;
  - expiry of rewards nobody collects.
- **Points expiry** (oldest first), **staff adjustments** with a note and an actor, **levels** from lifetime points, and **programme stats**.
- **Streaks and achievements** as pure functions over your own data. No extra tables.
- **Multi-tenant by design.** Every wallet, rule and reward belongs to a tenant. For a single-tenant app, use `Guid.Empty`.

The engine has no UI, no leaderboards and no cash value. It is the accounting underneath those features.

## Example: an online learning platform

Each school is a tenant. Learners earn points like this:

| Event | Points | Rule |
|---|---|---|
| `LessonCompleted` | 50 | up to 3 a day |
| `QuizPassed` | 20 | `{"score":{"gte":80}}` |
| `StreakMilestone` | 150 | `{"streakDays":7}` |

Northbridge Academy changes two things:

- It adds 10 points for lessons finished before 7am, in its own time zone.
- It caps earning at 300 points a day.

A learner spends 500 points on a 1:1 tutor session and reads the code `RW-7K3QX9` to staff. If staff cancel the session, the points come back once and the slot returns to stock.

The [sample](samples/ModelHub.Gamification.Sample) is this platform as a runnable minimal API. Try it with [sample.http](samples/ModelHub.Gamification.Sample/sample.http).

## Quick start

### 1. Add the tables

```csharp
public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyGamificationModel(); // optional: schema, table prefix
        // ... your own model
    }
}
```

Then add a migration: `dotnet ef migrations add Gamification`.

### 2. Register the services

```csharp
builder.Services.AddGamification<AppDb>(o =>
{
    o.DefaultRules.Add(new RuleDefinition("LessonCompleted", 50) { MaxPerDay = 3 });
    o.DefaultRules.Add(new RuleDefinition("QuizPassed", 20) { Condition = """{"score":{"gte":80}}""" });
    o.DefaultRules.Add(new RuleDefinition("StreakMilestone", 150) { Condition = """{"streakDays":7}""" });
    o.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); // days and weeks follow this
});
```

This registers four scoped services:

- `IPointsEngine`
- `IWalletService`
- `IRewardService`
- `IProgramService`

### 3. Report events after your own save

```csharp
db.QuizAttempts.Add(attempt);
await db.SaveChangesAsync();                           // your work first

var result = await points.HandleAsync(
    new GamificationEvent(tenantId, userId, "QuizPassed", attempt.Id.ToString())
    {
        Context = new Dictionary<string, object?> { ["score"] = attempt.Score },
    });
// result.Outcome: Credited, AlreadyCredited, NoMatchingRule, LimitReached or EarningDisabled
```

The reference (`attempt.Id`) is what makes the event pay only once. Use the id of the thing that caused the event, never a new GUID.

### 4. Show and spend points

```csharp
var summary = await wallets.GetSummaryAsync(tenantId, userId);     // balance, lifetime, level
var catalogue = await rewards.GetCatalogueAsync(tenantId);          // what this user can redeem now
var redeemed = await rewards.RedeemAsync(tenantId, userId, rewardId);
// redeemed.Redemption.Code == "RW-7K3QX9"

await rewards.FulfilAsync(tenantId, redemptionId, actor: staffId);  // handed over
await rewards.CancelAsync(tenantId, redemptionId, actor: staffId);  // refunded once, stock restored
```

### 5. Run the daily jobs

```csharp
await wallets.ExpireAsync();          // points older than each tenant's PointsExpireAfterDays
await rewards.ExpirePendingAsync();   // redemptions not collected within PendingRedemptionLifetime
```

The sample runs both jobs from a `BackgroundService`.

## Rules

A rule gives points for an event type. When several rules match one event, their points are added together.

**Conditions** are JSON objects matched against the event's `Context`. Every key must match:

| Condition | Matches when |
|---|---|
| `{"streakDays":7}` | equal. Numbers compare as numbers and text ignores case. |
| `{"score":{"gte":80}}` | the comparison holds: `eq`, `ne`, `lt`, `lte`, `gt` or `gte` |
| `{"localHour":{"gte":5,"lt":7}}` | both comparisons hold |
| `{"dayOfWeek":"Saturday","premium":true}` | both keys match |

The engine adds two keys to the context, both in the configured time zone, unless you supply them yourself:

- `localHour` (0–23)
- `dayOfWeek`

A key that is missing from the context never matches. A malformed condition never matches either. `RuleConditions.IsValid` and `SaveRuleAsync` reject malformed conditions before they are stored.

**Per-day limits.** `MaxPerDay` counts how many times this event type has paid the user today. Days follow the configured time zone, by when the event happened (`OccurredAtUtc`). So an event reported late counts toward the day it happened.

**Caps.** `DailyEarnCap` and `WeeklyEarnCap` are tenant settings. When an event would go over a cap, its points are trimmed to what is left, and the event after that gets `LimitReached`. Awards don't count toward caps.

**Tenant overrides.** If a tenant has any rules for an event type, those rules replace all the defaults for that event type. This has two consequences:

- A tenant that wants to add a bonus restates the base rule as well. The sample's Northbridge Academy does this.
- A tenant can switch an event type off by saving one disabled rule for it. Deleting its rules brings the defaults back.

`GetEffectiveRulesAsync` shows each tenant's rules and defaults together, for an admin screen or a "how to earn" page.

## Wallets and the ledger

| Ledger reason | Amount | Written by |
|---|---|---|
| `Earn` | + | `IPointsEngine.HandleAsync`, through the rules |
| `Award` | + | `AwardAsync`: a fixed amount such as a prize, outside the rules and caps, paid once per reference |
| `Redeem` | − | `RedeemAsync` |
| `Refund` | + | `CancelAsync` and `ExpirePendingAsync`, once per redemption |
| `Expire` | − | `ExpireAsync`, oldest points first |
| `Adjust` | ± | `AdjustAsync`, with a required note and actor. It can't take the balance below zero. |

**Levels** come from lifetime points: earned points plus positive adjustments. Spending a reward never costs a user a level. `LevelTable.Default` has 10 levels:

- The thresholds are 0, 100, 250, 500, 1k, 2k, 5k, 10k, 25k and 50k.
- After 50k there is one more level every 25k.

Pass your own `LevelTable` to change it.

## Streaks and achievements

These are plain functions over data you already have, so you don't need extra tables.

```csharp
var days = attempts.Select(a => DateOnly.FromDateTime(a.CompletedAtLocal));
var streak = Streaks.Current(days, today);           // counts back from today, or from yesterday
if (streak.Days == 7)
    await points.HandleAsync(new GamificationEvent(tenantId, userId, "StreakMilestone",
        $"streak:{streak.Start:yyyy-MM-dd}:7") { Context = new() { ["streakDays"] = 7 } });
```

The reference names the run's first day. So a single run pays its 7-day bonus once, and a new run can pay again.

`Streaks.Week` builds a Monday-to-Sunday view. Keep counting the streak itself across weeks: if it resets every Monday, it can never reach a 30-day milestone.

```csharp
var status = Achievements.Evaluate(definitions, new Dictionary<string, long>
{
    ["lessons"] = 42, ["bestStreak"] = 9, ["lifetimePoints"] = summary.LifetimeEarned,
});
```

## How it stays correct

- **Pays once.** Every credit, spend and refund carries an idempotency key, and the database enforces it with a unique index on `(WalletId, IdempotencyKey)`.
  - If two copies of the same event race, one of them loses at the database and comes back as `AlreadyCredited`.
- **No overspending.** `PointsWallet.Balance` and `Reward.Stock` are concurrency tokens.
  - If two learners race for the last item, one gets it and the other gets `OutOfStock`. Neither is charged for nothing.
- **One save per redemption.** The points, the stock and the redemption change together or not at all.
  - Cancelling works the same way: the refund and the restored stock save together.
- **Retries stay inside the library.** After a failed save, the services detach only their own entities, re-read and try again (three attempts). Your app's pending changes are left alone.
- **UTC throughout.** Times are stored in UTC and come back marked as UTC. Day and week boundaries follow `GamificationOptions.TimeZone`, including days when the clocks change.

## Options

| Option | Default | |
|---|---|---|
| `DefaultRules` | none | Rules every tenant earns with |
| `TimeZone` | UTC | Days and weeks for limits, caps, `localHour` and `dayOfWeek` |
| `FirstDayOfWeek` | Monday | When weekly caps reset |
| `Levels` | `LevelTable.Default` | Levels by lifetime points |
| `RedemptionCodePrefix` | `RW-` | Codes avoid 0/O and 1/I, so people can read them out loud |
| `RedemptionCodeLength` | 6 | 4 to 12 characters after the prefix |
| `PendingRedemptionLifetime` | 30 days | After this, uncollected redemptions expire and are refunded |

Tenant settings live in `PointsProgram`, which you manage with `IProgramService`:

- `EarningEnabled` and `RedemptionEnabled`
- `DailyEarnCap` and `WeeklyEarnCap`
- `PointsExpireAfterDays`

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md). The test suite runs on .NET 8 and .NET 10.

## Licence

MIT © ModelHub Solutions Ltd
