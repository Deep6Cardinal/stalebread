# StaleBread

A small Windows companion for understanding the process activity left behind by a busy AI-assisted workflow. An animated slice of toast watches for breadcrumbs, then pops out of its toaster when there is something worth reviewing.

**Unsigned preview · Windows 11 x64 · Local-first · No automatic process termination**

## Why this program exists

Working across several AI coding or chat sessions can turn a simple desktop into a surprisingly complicated process tree: PowerShell windows, terminal hosts, Codex or Claude sessions, Node workers, MCP servers, and browser helpers. Some are doing useful work. Others may outlive the session that needed them. When the machine starts feeling sluggish, telling those two groups apart in Task Manager is frustrating—and guessing can destroy live work.

StaleBread grew out of that recurring friction. Cleaning up genuinely abandoned helpers can help, but **not every slowdown is caused by stale processes**, and a large process count is not evidence of a leak. A busy model client, build, or browser can be entirely legitimate. Old, idle, windowless, or memory-heavy does not mean safe to stop.

The goal is visibility first: show what appeared and disappeared, preserve useful evidence, distinguish busy workloads from possible leftovers, and make any cleanup a deliberate review. StaleBread is a companion, not a RAM booster, antivirus, or an automatic “kill everything old” utility.

The toast is part of that idea: a friendly, glanceable reminder that there may be breadcrumbs to inspect, rather than another alarming system dashboard. Its notification uses an actual recorded toaster pop, with attribution below.

## What it does

- Watches console and AI-related process activity, with an animated desktop companion and tray controls.
- Shows current watched-process counts and separate appeared/disappeared totals over 1, 5, 15, 30, and 60 minutes. **`+20 / −20` remains visible even when the net change is zero.**
- Reports possible leftovers, busy workloads, resource growth, subprocess churn, and sustained helper accumulation. Activity advisories are not measured slowdown scores.
- Includes numeric trigger evidence, thresholds, measurement time, and process/group/family scope in diagnostic JSON schema 2. Findings can overlap; do not add their totals together.
- Requires two minutes of qualifying accumulation before its first alert. Churn and accumulation use a two-minute clearing period, shown as **Settling**, to reduce alert flapping.
- Offers quiet hours, mute/volume, reduced motion, exclusions, and optional startup. Startup is off by default.

Sampling normally runs every five seconds. Optional process-start/stop events improve visibility; snapshot monitoring is a supported fallback when events are unavailable. Short-lived processes can be missed, so activity counts are **observed lower bounds**, not a complete ledger. Sustained monitoring overhead can switch the app to a slower economy mode.

Memory figures are **private committed bytes**, not free physical RAM or a promise of what stopping a process would reclaim.

## Safety and privacy

StaleBread never automatically stops a process. Eligible cleanup requires selecting individual helpers, reviewing a frozen proposal, and explicitly confirming force-stop. Identity and safety checks are repeated immediately before acting. Active AI roots, terminal hosts, unknown ownership, and protected descendants are excluded. Incomplete ancestry or an unrecognized installation can make an item review-only; administrator mode is not a bypass. **Force-stopping a process can lose unsaved work.**

There is no account, cloud service, telemetry, or runtime AI dependency. Monitoring stays on your machine. Chats, keystrokes, and raw command arguments are not retained. Settings and bounded history live in `%LOCALAPPDATA%\StaleBread`; history is limited to seven days/20 MiB. Export shows a sanitized preview and excludes private paths, account names, arguments, and chat/session titles. Review exports before sharing them.

## Download and run

Compiled Windows x64 previews will be attached to [GitHub Releases](https://github.com/Deep6Cardinal/stalebread/releases) when published. Download the preview ZIP and **extract the entire folder**, including its licenses and notices—not just the EXE. Run `StaleBread.exe` as your ordinary user. The self-contained package bundles .NET.

The preview is unsigned, so Windows may warn about it. Do not disable Windows security to run it. Closing the companion hides it in the tray; choose **Exit StaleBread** from the tray menu to stop it.

See [the usage guide](docs/USAGE.md) for findings, cleanup, export, settings, and removal instructions.

## Build from source

Use Windows with the .NET SDK selected by [global.json](global.json): **10.0.403**, allowing later patches in its SDK feature band. No environment variables or credentials are required. NuGet restore needs access to the public package feed.

```powershell
./setup.ps1
./scripts/publish.ps1
./scripts/verify-package.ps1 -PackagePath dist/win-x64
```

The bootstrap performs locked restore, Release build, and tests. Publishing produces `dist/win-x64/StaleBread.exe`, licenses, usage documentation, and a portable SHA-256 checksum. Normal builds and self-contained publishing use separate dependency lock files. Git Bash users on Windows can run `./setup.sh`; this is not a Linux/macOS application.

## Preview status

The staging source passed **326 automated tests: 236 Core and 90 Windows**. Automated checks do not certify desktop usability or safety in every process tree.

Still-open acceptance work includes visual/audio behavior, DPI layouts, tray keyboard accessibility, a clean Windows machine without .NET installed, matched native-provider overhead measurements, and long real-hardware sessions. Synthetic replay is not a substitute for an eight-hour real-machine test. Treat this as an experimental preview, not a production-certified release.

## Contributing and licensing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development and privacy-conscious bug reports, and [CLAUDE.md](CLAUDE.md) for architecture and safety invariants.

Program code and original artwork are [MIT licensed](LICENSE), copyright 2026 Deep6Cardinal. The recorded sound **“Toaster Pop, A.wav” by InspectorJ (Jonathan Shaw)** is separately licensed **CC BY 4.0**; attribution and modifications are documented in [asset credits](src/StaleBread.App/Assets/LICENSES.md). Microsoft runtime and other third-party notices remain under their respective terms; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). No endorsement by those creators is implied.
