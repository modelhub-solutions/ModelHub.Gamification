# Changelog

## 0.1.0 (unreleased)

First public release, extracted from the rewards engine in the StrideWell API.

### ModelHub.Gamification

- `GamificationEvent` and `RuleDefinition`: string event types, user ids and references
- `RuleConditions`: JSON conditions with equality and `eq`, `ne`, `lt`, `lte`, `gt`, `gte`, plus `IsValid` for checking a condition before saving it
- `LevelTable`: levels from lifetime points, with progress to the next level
- `Streaks`: current and longest runs, and a weekly view
- `Achievements`: evaluates achievement definitions against your metrics
- `GamificationOptions`: default rules, time zone, first day of the week, levels and redemption codes

### ModelHub.Gamification.EntityFrameworkCore

- `ApplyGamificationModel`: tables for programmes, rules, wallets, the ledger, rewards and redemptions
- `IPointsEngine`:
  - matches rules, per-day limits and daily or weekly caps in a configurable time zone
  - pays once per event type and reference, enforced by a unique index
- `IWalletService`: summaries, history, awards, staff adjustments, and points expiry (oldest points first)
- `IRewardService`:
  - catalogue with stock and availability windows
  - redemption in a single save, with readable codes
  - fulfil or cancel (refunded once, stock restored)
  - expiry of uncollected redemptions
- `IProgramService`: tenant settings, rule overrides, effective rules and stats
- Optimistic concurrency on balances, stock and redemption status, with retries that leave the app's own pending changes alone
- `AddGamification<TContext>`
- Sample minimal API (online learning platform) and a test suite on .NET 8 and .NET 10
