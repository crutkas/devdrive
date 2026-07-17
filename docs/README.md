# DevDriveManager — documentation

DevDriveManager is a WinUI 3 prototype that reimagines the Windows Settings **Dev Drive**
experience: detect and inspect Dev Drives, create new ones, move package caches onto them, and
measure the real performance difference — safely, with honest numbers, on *your* machine.

This folder documents how it works and **why** each option exists. The code is always the source of
truth; every claim below is grounded in a named file you can open and verify.

## Read this first

| Doc | What it covers |
| --- | --- |
| [Configuration.md](Configuration.md) | **Every setting and option, and why it's there** — Defender performance mode, benchmark tunables, the safe‑mutation seam, where reversible state is stored. |
| [CreatingADevDrive.md](CreatingADevDrive.md) | **Creating a Dev Drive** — the two code paths (new VHDX vs. resize an existing volume), all of the options, and exactly what is *real* vs. *simulated*. |
| [PackageCacheMoves.md](PackageCacheMoves.md) | **Moving package caches** onto the Dev Drive — the catalogue, the copy‑then‑commit mechanism, and the **documented fallbacks** for every failure path (rollback, idempotency, Move back). |
| [SpeedTest.md](SpeedTest.md) | **The performance tests** — what each test measures, why, the methodology, and how missing tools are flagged. |
| [Testing.md](Testing.md) | **Test-machine setup and self-hosting** — build lanes, tool/cache population, safe UI automation, and disposable-VM storage workflows. |
| [KnownIssues.md](KnownIssues.md) | **Current readiness blockers** — severity, evidence, and the next action for each confirmed issue. |

## The through‑line (how this is built)

- **Backport‑ready split.** *All* logic lives in the UI‑agnostic `DevDriveCore` library, behind
  interfaces, with native/WMI/process calls isolated in `DevDriveCore/Platform`. The WinUI app
  (`DevDriveManager`) is a thin MVVM shell. That keeps every behaviour unit‑testable without XAML and
  lets the logic later drop into the real Settings handler.
- **Safety-first.** Detection and inspection are **real**. Package-cache changes are explicitly
  confirmed, per-user, recorded, and reversible. Storage creation requires confirmation and UAC;
  resize uses one confirmation and one elevated helper transaction that verifies and binds live disk
  identity before mutation. The helper revalidates that identity immediately before shrinking. Benchmarks
  run in bounded, self-cleaning temporary folders and never touch real caches, settings, or partitions.
- **Honest numbers.** The app uses your actual machine and tools. It never fabricates a metric, and
  it surfaces the caveats that explain a result (for example, whether Defender performance mode is in
  effect). Where it can't determine something, it says so instead of guessing.

## The safe‑mutation seam (how mutations are tested without touching your machine)

Every real mutation engine sits behind an interface. When the environment variable
`DDM_UITEST_SAFE_MUTATIONS=1` is set, the app composes **in‑memory fakes** instead of the real
engines (`DevDriveManager/Services/MutationComposition.cs`), and a visible *"safe mutation mode"*
indicator appears in the UI (`MainPageViewModel.IsSafeMutationMode`). This is how the automated UI
tests exercise *Move / Move back / Confirm* flows end‑to‑end **without** changing a single real file,
environment variable, or setting. In normal operation the real engines are used.

## Where the pieces live

| Concern | Project / folder |
| --- | --- |
| UI‑agnostic logic, models, interfaces | `DevDriveCore/` (`Services`, `Models`, `Abstractions`) |
| Real native/WMI/process implementations | `DevDriveCore/Platform/` |
| WinUI 3 shell (pages, ViewModels) | `DevDriveManager/` |
| Bundled elevated filter, resize, and VHDX helper | `DevDriveManager.FilterProbe/` |
| Unit tests | `DevDriveCore.Tests/` |
| Automated UI tests | `DevDriveManager.UITests/` |

> If the code and any doc here ever disagree, **the code is the source of truth** — please update the
> doc (or file an issue).
