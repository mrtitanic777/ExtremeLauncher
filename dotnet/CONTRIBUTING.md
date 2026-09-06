# Contributing to the .NET port

Thanks for helping. This covers the C#/.NET 9 + Avalonia port under [`dotnet/`](.); the Qt/C++ launcher
at the repository root has its own `CONTRIBUTING.md`.

## Before you open a pull request

The default branch is protected: **you cannot push to it directly.** Fork the repo (or push a branch)
and open a pull request. Every PR is reviewed by the maintainer, and it can only merge once:

1. **It builds and the tests pass.** CI runs the whole solution on Linux and Windows and must be green.
2. **The maintainer has approved it.**

Run the same checks locally first — from `dotnet/`:

```bash
dotnet build ExtremeLauncher.sln -c Release
dotnet test  ExtremeLauncher.sln -c Release
```

A green local run on your OS is the minimum; CI covers the other one.

## What good changes look like here

This port has a house style, visible in any file and in [`PORTING.md`](PORTING.md):

- **The Qt source is the contract.** Port behaviour faithfully; where you diverge, say so in a comment
  and a test, with the reason.
- **Logic lives in a testable library, not in a window.** View models are framework-free and unit
  tested; `ExtremeLauncher.App` only wires them to Avalonia. Put behaviour where it can be tested.
- **A change comes with tests.** New behaviour gets tests beside it in the matching
  `tests/ExtremeLauncher.*.Tests` project. A guard is not covered until a test fails when it is removed.
- **Cross-platform by default.** No backslash path assumptions, no "open files block deletion" (that is
  Windows-only), no reflink/trash assumptions. If something is platform-specific, gate it with
  `[SkippableFact]` + `Skip.If`/`Skip.IfNot`.
- **No secrets in source.** API keys are supplied at runtime; never commit a `credentials.json` or a key.
- **Zero warnings.** The build is warning-clean; keep it that way.

## Scope

Keep a PR to one coherent change. Large, mixed PRs are hard to review and slow to land. If you are
unsure whether something fits, open an issue first and ask.
