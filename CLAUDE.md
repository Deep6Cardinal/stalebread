# StaleBread contributor/agent guide

StaleBread is a Windows 11 x64, .NET 10 desktop companion for local AI/console-process observation. It prioritizes evidence and live-work safety over aggressive cleanup. Read README.md and docs/USAGE.md before changing user-visible behavior.

## Commands

Run from the repository root on Windows with the SDK in global.json:

```powershell
./setup.ps1
dotnet test -c Release --no-restore
dotnet run --project src/StaleBread.App -c Release --no-build
./scripts/publish.ps1
./scripts/verify-package.ps1 -PackagePath dist/win-x64
dotnet run --project tests/StaleBread.Soak -c Release --no-restore
git diff --check
```

The soak command is a synthetic replay, not a real-duration desktop soak. setup.sh is a Git Bash wrapper for setup.ps1 on Windows. No credentials/environment configuration is needed. Normal and self-contained publish restores use separate lock files. Generated bin/obj/dist files do not belong in source control.

## Architecture

- `src/StaleBread.Core`: platform-independent models, temporal graph, association evidence, detector rules, rolling count history, notification policy, storage/export, and cleanup policy/coordinator.
- `src/StaleBread.Windows`: native process handles and snapshots, metadata/event collection, desktop/startup integration, and PCM audio support.
- `src/StaleBread.App`: WPF companion/findings/settings windows, WinForms tray integration, monitoring orchestration, notification/audio presentation, and dependency composition.
- `tests/StaleBread.Core.Tests`: rules, stability, graph, counts, privacy/export, and cleanup invariants.
- `tests/StaleBread.Windows.Tests`: native integration, controlled child fixtures, presentation, collection, and audio checks.
- `tests/StaleBread.TestChild`: contained integration-test helper, never a consumer package component.
- `tests/StaleBread.Soak`: bounded synthetic long-window replay.

Key entry points: `AppComposition.cs`; `Services/MonitorService.cs`; Core `Detection/RuleEngine.cs` and `RuleOptions.cs`; `Cleanup/CleanupPolicy.cs` and `CleanupCoordinator.cs`; Windows `Native/ProcessHandleFactory.cs`; Core `Storage/DiagnosticExporter.cs`; App `ViewModels/DiagnosticText.cs`.

## Non-negotiable invariants

No automatic termination. Cleanup requires explicit per-identity selection, a frozen proposal, fresh identity/eligibility checks, and native handle safeguards. Protect live AI roots, terminal hosts, unknown ownership, and protected descendants. Missing ancestry or metadata must not make cleanup easier. Never equate age, idleness, no window, or high memory with safe termination.

Snapshot coverage is supported and counts are observed lower bounds. Gross appeared/disappeared counts must survive net-zero changes. Private bytes measure commit, not physical RAM freed. Findings may overlap in scope. Churn/accumulation are advisories, not demonstrated performance impact. Preserve persistence/settling behavior and distinguish last matching evidence from current observations.

Settings/history remain local. Never collect chats or keystrokes, retain raw arguments, or add telemetry without explicit project direction. Export uses a positive allowlist of fields/codes and export-local aliases. Old history without numeric evidence cannot be retroactively reconstructed. Keep diagnostics and personally identifying examples out of commits.

## Release discipline

The current build is an unsigned preview. Desktop visual/audio/DPI/tray-keyboard acceptance, clean-machine runtime independence, matched provider overhead, and long real-hardware testing remain open. Do not rewrite these as passed based on automated tests. Package the MIT LICENSE, third-party notices, sound attribution, upstream license texts, and portable checksum alongside the EXE. Do not recommend disabling Windows security.

Use focused patches and regression tests. Preserve existing user changes. Do not commit or publish private preparation reports, raw diagnostics, build outputs, or secrets.
