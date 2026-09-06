## What this changes

<!-- One or two sentences. What does this PR do, and why? -->

## For .NET port changes (dotnet/)

- [ ] `dotnet build ExtremeLauncher.sln -c Release` is clean (no warnings)
- [ ] `dotnet test ExtremeLauncher.sln -c Release` passes locally
- [ ] New behaviour has tests beside it (and any guard fails without them)
- [ ] No platform assumptions (paths, open-file locks, trash/reflink) — platform-specific tests are gated with `Skip.If`/`Skip.IfNot`
- [ ] No secrets or `credentials.json` committed

## Notes for the reviewer

<!-- Anything that helps review: deliberate divergences from the Qt source, what is NOT covered, follow-ups. -->
