# Configuration — every setting and why it exists

This page documents each setting and option the app exposes, what it does, what it changes (if
anything), and the reasoning behind it. Settings fall into two buckets:

1. **Detection / read‑only context** — never changes anything.
2. **Reversible, per‑user actions** — preview → Confirm, undoable.

---

## 1. Microsoft Defender performance mode  *(read‑only detection + one reversible, elevated action)*

A Dev Drive's real advantage comes from Defender **performance mode**: trusted Dev Drives are scanned
**asynchronously**, off the hot path, instead of synchronously like a normal volume. The app *detects*
this state and, only when it's genuinely off, offers to turn it on.

### Detection (read‑only, reliable)

- **Global preference** — `Get-MpPreference`'s `PerformanceModeStatus`, read via
  `DevDriveService.GetDefenderPerformanceMode()` and parsed by `PreflightProbe.ParsePerformanceMode`.
  The integer mapping is **`1` = Enabled (on / asynchronous)**, **`0` = Disabled (off / synchronous)**.
  This is the **inverse** of the Intune/CSP OMA‑URI integer, and it was verified empirically (on a
  machine with performance mode on, `[int](Get-MpPreference).PerformanceModeStatus` reads `1`, matching
  Windows Security's *Dev Drive protection → See volumes*). The preference is **reliable** and
  **readable without administrator**.
- **Per‑volume trust** — `fsutil devdrv query <letter>:`, parsed by `FsutilDevDrvParser`. This needs
  administrator, so the app reads it via the elevated, **read‑only** *See Filters* helper
  (`DevDriveManager.FilterProbe`) launched on demand — no full app restart required.
- **The verdict** — `PerformanceModeEvaluator.Evaluate(trust, globalPerfOn, isDevDrive)` combines them
  into an honest `EffectivePerformanceMode`:
  - For a detected Dev Drive (even unelevated) the reliable global preference reports **On (async)** /
    **Off (sync)**.
  - When trust shows the volume's protection is **enforced by group policy**, the verdict adds
    *"managed by your organization"* and **suppresses the local "turn on" lever** (you can't flip a
    policy‑managed setting locally). If the preference can't be read at all, it degrades to
    *Managed* / *Unknown* rather than guessing.
  - **Attached antivirus minifilters (WdFilter / MsSecFlt) are deliberately ignored** as a scan‑mode
    signal — they attach in *both* async and sync modes, so their presence says nothing about whether
    performance mode is on.

> The authoritative per‑volume source is always **Windows Security → Dev Drive protection → "See
> volumes"**; the app points you there rather than over‑claiming.

### The one mutating action

When performance mode is **genuinely off and not policy‑managed**, the app offers a single, reversible,
**UAC‑elevated** action under **Drive health → Trust & filters**:

```
Set-MpPreference -PerformanceModeStatus Enabled     # turn on  (offered)
Set-MpPreference -PerformanceModeStatus Disabled    # the documented reverse
```

It is never applied silently — it requires explicit confirmation and administrator approval. There's
also a deep link to Windows Security (`windowsdefender://threatsettings/`) for the managed case. The
copy and commands live in `DevDriveCore/Services/PerformanceModeAdvisor.cs`.

---

## 2. Performance‑test tunables  *(`WorkloadBenchmarkOptions`)*

Defined in `DevDriveCore/Services/IWorkloadBenchmarkService.cs`. These govern how the performance suite
runs (see [SpeedTest.md](SpeedTest.md) for the full methodology).

| Option | Default | Why |
| --- | --- | --- |
| `Iterations` | **3** | Warm‑cache runs per drive. The **first run is discarded** (it primes the OS/file cache) and the **median** of the rest is reported. 3 keeps a full run bounded while preserving discard‑first + median. Minimum 1. |
| `GlobalProfile` | **Thorough** | Fixture size for the "Real‑world builds" card: realistic, real‑world projects that churn enough small files to actually expose the Dev Drive's advantage. |
| `InlineProfile` | **Quick** | Smaller, responsive fixtures. Retained in the library; it backed the now‑removed inline per‑cache test, so it's effectively vestigial in the UI. |

---

## 3. Reversible‑action state  *(where "undo" lives)*

Any reversible mutation records a `ReversibilityEntry` so it can be undone later — including across app
restarts.

- **Store:** `DevDriveCore/Platform/JsonFileReversibilityStore.cs`.
- **Location:** `%LocalAppData%\DevDriveManager\reversibility.json`.
- **What it is:** app‑local bookkeeping that records *what an engine changed* (prior value, source,
  target) so it can be reverted. Writing this file is **not itself a machine mutation**; it's read
  lazily and only written on save/remove.

This is what makes **Move back** appear on a package‑cache row even after you close and reopen the app
(`PackageCacheMoveCoordinator.CanMoveBack` reads this store).

---

## 4. The safe‑mutation seam  *(test‑only)*

Environment variable: **`DDM_UITEST_SAFE_MUTATIONS`** (`DevDriveManager/Services/MutationComposition.cs`).

When set to `1`, the app composes **in‑memory fake** mutation engines instead of the real ones, and a
visible *"safe mutation mode"* indicator appears (`MainPageViewModel.IsSafeMutationMode`). This exists
**only** so automated UI tests can drive *Move / Move back / Confirm* without touching real files,
environment variables, or settings. **It is never used in normal operation** and is not a user
setting — don't set it yourself.

---

## 5. The package‑cache catalogue  *(what can be moved, and the variable that moves it)*

`DevDriveCore/Services/PackageCacheCatalog.cs`. Each entry is the **documented, real** relocation
environment variable and default location for that tool. See [PackageCacheMoves.md](PackageCacheMoves.md)
for how the move works.

| Tool | Environment variable | Default location |
| --- | --- | --- |
| npm | `npm_config_cache` | `%LocalAppData%\npm-cache` * |
| NuGet global packages | `NUGET_PACKAGES` | `%UserProfile%\.nuget\packages` |
| pip | `PIP_CACHE_DIR` | `%LocalAppData%\pip\Cache` |
| Cargo | `CARGO_HOME` | `%UserProfile%\.cargo` |
| vcpkg | `VCPKG_DEFAULT_BINARY_CACHE` | `%LocalAppData%\vcpkg\archives` |
| Gradle | `GRADLE_USER_HOME` | `%UserProfile%\.gradle` |
| Go modules | `GOMODCACHE` | `%UserProfile%\go\pkg\mod` |
| Yarn | `YARN_CACHE_FOLDER` | `%LocalAppData%\Yarn\Cache` |

> \* The npm cache location is resolved at runtime by `NpmCacheLocator` in this order:
> `npm_config_cache` → `npm config get cache` → the `%LocalAppData%\npm-cache` default. The template
> above is only the last‑resort fallback. *"NuGet global packages"* is the single folder shared by
> NuGet, `dotnet`, MSBuild, and Visual Studio, so it's listed once rather than duplicated per consumer.

---

## 6. Read‑only context captured before a run (pre‑flight)

Before the performance suite runs, `PreflightProbe` captures read‑only context so a result is
interpretable — Defender performance mode, Dev Drive trust + attached filters, storage class
(media/bus, via Storage WMI), CPU/OS summary, and free space on both drives. **This changes nothing.**
