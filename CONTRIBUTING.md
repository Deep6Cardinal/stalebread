# Contributing to StaleBread

Contributions that make process activity easier to understand without endangering live work are welcome. Please discuss substantial detector or cleanup-policy changes in an issue first.

## Development

Use Windows 11 x64 and the .NET SDK selected by `global.json` (10.0.403 or a compatible later patch). From a checkout:

```powershell
./setup.ps1
dotnet run --project src/StaleBread.App -c Release --no-build
```

`setup.ps1` restores locked dependencies, builds Release, and runs tests. Test fixtures launch controlled child processes; do not point them at real user sessions. No environment variables, accounts, credentials, or elevation are required for the normal development workflow.

Before submitting a pull request:

```powershell
dotnet test -c Release --no-restore
git diff --check
```

For distribution changes, also run `./scripts/publish.ps1` and `./scripts/verify-package.ps1 -PackagePath dist/win-x64`. Do not commit `bin`, `obj`, `dist`, personal diagnostics, or local application history. Update both normal and publish lock files deliberately if a dependency changes; do not disable locked restore to hide a mismatch.

## Changes and tests

Keep pull requests focused and explain the observed problem, expected behavior, and verification performed. Add regression tests for changes to detection, lifecycle transitions, export privacy, or cleanup eligibility. Separate automated evidence from manual acceptance: a replay is not a real-machine soak, and a passing audio unit test does not prove a user heard the sound.

Preserve these safety boundaries:

- Never introduce automatic termination or use age, idleness, window visibility, or memory alone as cleanup authorization.
- Keep explicit selection, frozen proposals, fresh identity checks, and live-work protections.
- Treat missing metadata as unknown, not zero; incomplete evidence must not broaden cleanup eligibility.
- Preserve lower-bound count labels and distinguish observed memory from reclaimed memory.
- Do not log chats, keystrokes, raw arguments, or private paths into public diagnostic exports.
- Retain third-party license and attribution files in redistributed packages.

## Reporting bugs

Use the bug template with the preview version, Windows version, reproduction steps, and whether event tracing or snapshot fallback was active. Sanitized JSON is useful for detection issues, but inspect it yourself before attaching. Do not upload raw process command lines, account names, session titles, credentials, or your entire local-data folder. Describe a sensitive issue without exposing private data in a public issue; never post secrets as proof.

By submitting a contribution, you agree that your contribution may be distributed under the project's MIT license. Separately licensed third-party assets retain their original terms.
