# Contributing

Thanks for helping. Small, focused pull requests are easiest to review.

1. Before starting larger work, open an issue describing the change, or comment on an existing one.
2. Fork the repo, create a branch, and make the change with tests.
3. Run `dotnet test`. The suite runs on .NET 8 and .NET 10, against SQLite.
4. Open a pull request that links the issue (`Closes #123`).

Issues labelled **good first issue** are scoped for a first contribution.

## Ground rules

- Points are money-like, so every change to the ledger, wallets or redemptions needs a test that proves two things:
  - points can't be paid twice;
  - points can't be spent twice.
- Balances only change through the ledger. Don't write a balance directly.
- Keep the public API small. New options need a sensible, safe default.
- Be kind. We follow the [Contributor Covenant](https://www.contributor-covenant.org/version/2/1/code_of_conduct/).
