# Performance tests — what each test measures, and why

This document explains, in full, what DevDriveManager does when it measures performance — **what tests
what, and why.** Transparency is the point: every number the app shows is produced by the code described
here, running on *your* machine with *your* tools. Nothing is fabricated, nothing is hard‑coded, and
nothing about your system is changed to produce a result.

> **TL;DR**
> - The suite times **real developer builds** on your system drive vs. your Dev Drive: `git clone`,
>   `npm ci`, `dotnet build`, `cargo build`. Lower time wins; the app reports the ratio.
> - Each comparison is **symmetric and cold**: the package cache, the source, and the build output are
>   **all on the one drive being measured**, and the per‑drive cache is freshly populated **cold** before
>   every timed run — the realistic **CI / first‑build** case. See §1.
> - A Dev Drive's advantage shows up in **many‑small‑file** work under antivirus, not in raw MB/s — so
>   the old raw **disk‑speed** test was **removed** from the suite (it undersold the real benefit). See §4.
> - Each build uses the **real tool** (`git` is real `git`, etc.). If a tool isn't installed, that row is
>   **flagged as skipped** — never faked.
> - Everything is **bounded, runs in temporary folders, and is always cleaned up.** Your real caches,
>   settings, environment variables, and partitions are never touched.

---

## 1. The real‑world build tests

These time **real developer commands** on the system drive vs. the Dev Drive. Lower time is better; the
app reports the ratio (e.g. *"1.4× faster on your Dev Drive"*). Three of the four use a **real,
Microsoft‑owned project** pinned to a release (so we own the supply chain); `git clone` uses a
generated tree on purpose (see the note below).

| Row | What it builds / does | Real tool | Network |
| --- | --- | --- | --- |
| **git clone** | Clones a **generated** ~15,000‑file tree from a **local** bare repo (`git clone --no-hardlinks`) | `git` | **None** — fully offline by design |
| **npm ci** | Clean‑installs **Microsoft's `vscode-eslint`** (the VS Code ESLint extension, tag `release/3.0.24`) from its **committed** `package-lock.json` (~213 real packages) with `npm ci --offline` | `npm` | One‑time seed, then offline |
| **dotnet build** | Builds **Microsoft's `System.Reactive`** (`dotnet/reactive`, tag `rxnet-v6.1.0`) for `net6.0` | `dotnet` | One‑time seed, then offline |
| **cargo build** | Builds **Microsoft's `edit`** (`microsoft/edit`, tag `v2.0.0`, the Rust console editor) | `cargo` | One‑time seed, then offline |

**Why `git clone` stays a *generated* tree.** The clone itself is **100% real `git`** — `git init` /
`add` / `commit` / `clone --bare` and a timed `git clone --no-hardlinks`, all shelling out to the real
`git` executable (there is **no** managed git library anywhere in the solution). Only the *content* is a
disposable generated tree, cloned from a **local** repository so there's **no network** in the
measurement. `--no-hardlinks` forces real file copies even for the same‑filesystem (`C:`→`C:`) case so
the comparison is fair. Cloning locally isolates the variable we care about — git's working‑tree
checkout (thousands of tiny file writes under the antivirus filter) — instead of adding network latency
that would just cancel out across both drives.

> All four picks were validated to build **offline on this machine** before shipping. `System.Reactive`
> requires a **full** (non‑shallow) clone because it uses Nerdbank.GitVersioning, which rejects shallow
> clones; the workload handles that.

### The protocol: symmetric, cold, offline

Every timed run is the honest **"everything on one drive"** case. For the drive under test we put **all
three** of the things a real build touches on **that same drive**:

1. **The package cache** — the per‑drive cache directory the harness hands each workload, which lives on
   the drive being measured (not a shared cache on `C:`).
2. **The source** — copied fresh onto the drive for each iteration.
3. **The build output** — `node_modules` / `obj`+`bin` / `target/`, written onto the drive by the build.

And each timed run is **cold**, not warm steady‑state:

- A package cache is warmed **once**, offline‑of‑the‑timer, in the prepare step (the only network step —
  `npm install` / `dotnet restore` / `cargo fetch`). That **warm seed** lives in a neutral `%TEMP%`
  folder and is **never measured directly**.
- Before **every** timed iteration, the workload **copies that warm seed cold onto the drive under
  test** — a clean copy of just‑written files — then points the tool at that per‑drive copy. So what the
  timer sees is a **first build against a freshly‑written cache that lives on the drive being measured** —
  the realistic CI / fresh‑clone case. (Honest caveat: the per‑drive cache files are *freshly written*
  each run, so they are **cold for antivirus first‑access scanning** — the main effect we measure — but
  the app does **not** flush the **OS page cache**, so some reads may still be served from RAM. The cold
  signal here is the on‑disk freshness + the first‑access AV scan, not an evicted OS cache.)

This is what the per‑tool timed commands look like (each runs on each drive, with its cache on that
drive):

| Row | Untimed prepare (once, network) | Per‑iteration cold setup (untimed) | **Timed** command |
| --- | --- | --- | --- |
| **git clone** | `git init`/`add`/`commit`/`clone --bare` a generated tree (local, no network) | — (git has no package cache) | `git clone --no-hardlinks <local bare> <drive>\clone` |
| **npm ci** | `npm install … --cache <seed>` (writes the lockfile, warms the seed) | copy seed → `<drive>\…\cache`; copy `package.json` + `package-lock.json` | `npm ci --offline --cache <drive>\…\cache` |
| **dotnet build** | full `git clone`; `dotnet restore … (NUGET_PACKAGES=<seed>)` | copy seed → `<drive>\…\cache`; copy source (skip `bin`/`obj`); **offline re‑restore** (see below) | `dotnet build … -f net6.0 -c Release --no-restore  (NUGET_PACKAGES=<drive>\…\cache)` |
| **cargo build** | `git clone` the tag; `cargo fetch (CARGO_HOME=<seed>)` | copy seed → `<drive>\…\cache`; copy source (skip `target`/`.git`) | `cargo build --offline -q  (CARGO_HOME=<drive>\…\cache)` |

> **Why `dotnet` needs an untimed offline re‑restore.** `dotnet build --no-restore` reads packages from
> the **absolute path baked into `obj/` at restore time** — setting `NUGET_PACKAGES` at *build* time does
> **not** re‑point those reads. So the workload deliberately **does not copy the seed's `obj/`**; instead,
> each iteration runs a fast, fully‑offline `dotnet restore … --source <empty-dir>` that resolves every
> package from the just‑copied **per‑drive** cache and regenerates `obj/` pointing at it. `--source
> <empty-dir>` overrides all feeds, guaranteeing **no network**. That re‑restore is **setup, not
> measured** — only the subsequent compile is timed. Without it, the Dev‑Drive run would read its packages
> from the seed cache on `C:` — the exact asymmetry this protocol fixes.

### Methodology

For each row:

1. **Prepare once** — clone/seed a bounded fixture and warm its package cache in `%TEMP%` (the only
   network step).
2. **Measure N times on the system drive, then N times on the Dev Drive** (one drive fully before the
   other). Each iteration re‑populates the per‑drive cache **cold** first (untimed), then times the
   build/install.
3. **Discard the first run on each drive** (it primes process start‑up / JIT / OS state) and report the
   **median** of the remaining runs.

By default **N = 3** (3 runs per drive, first discarded, median of 2 — `WorkloadBenchmarkOptions`).

> **Surfaced in the app (transparency).** Every row has a **Details** affordance showing, inline: a
> plain‑language description of the operation, the **exact command** run, **which real project** it
> clones/installs/builds, this methodology, **and the individual raw run times for each drive**
> (including the discarded first run) — so the spread behind the median is visible, not just the headline.

### Why this is the fair comparison

An earlier version of the suite **pinned every package cache on `C:`** and only varied the *output*
drive. That made the Dev‑Drive run **asymmetric**: the source was on `G:` but the cache it read from was
still on `C:`. Two problems with that:

- It was **challengeable as cherry‑picked** — half the I/O (the cache reads) never moved to the drive
  under test, so the comparison wasn't really "C: vs the Dev Drive," it was "C: vs a mix."
- It measured **roughly the same thing** as just changing the output folder, hiding the cache‑read cost.

The protocol above removes that asymmetry: **everything is on the respective drive**, so each run is an
honest *"everything on `C:`"* vs *"everything on the Dev Drive"*. That's both the fairer comparison and
the one that matches a real first build on a freshly‑provisioned drive.

---

## 2. Gating and graceful skips — every tool must be the real tool

A row only runs if its tool is **installed**, detected by probing for the executable
(`InstalledToolDetector` runs `git --version`, `npm --version`, `dotnet --version`, `cargo --version`;
catalogue in `InstalledToolCatalog`). If a tool isn't found, that row is **skipped with a reason**:

> *"git is not installed"* · *"cargo is not installed"* · …

This is the explicit answer to *"what if I don't have git?"* — the git row is **flagged**, never silently
dropped and never replaced with a fake clone. The same gating applies to `npm` / `dotnet` / `cargo`.
Low disk space, no network during the one‑time seed, a timeout, or any tool failure likewise produce a
**skip with a reason** rather than a crash or a fake number.

> Git clone tolerates a single transient failure (real‑time antivirus can briefly lock a just‑written
> file during checkout — *exactly* the effect we're measuring) by retrying a bounded number of times; a
> persistent failure still skips honestly.

---

## 3. The "Suggestions" panel (the upside, not a measurement)

Below the build rows sits a **Suggestions** panel (formerly "You could do more"), relocated to the
bottom of the page. It names the concrete levers — honestly, without pretending to be a second
measurement:

- **Move your package caches to the Dev Drive** (links to Package caches).
- **Put your source + build output on the Dev Drive** — usually the bigger win.
- **Turn on Defender performance mode** — *shown only when it's genuinely off and not policy‑managed*
  (see §5).

An earlier build also showed a separate **"Package caches" benchmark group** with a per‑cache *Test
speed* button. It was **removed** because it was misleading: it pinned the package cache on `C:` and only
varied the output drive — i.e. it measured the *same thing* as the build rows, so it looked like a
second independent result when it wasn't. (The build rows themselves used to share that same cache‑on‑`C:`
shortcut; §1 explains how the current protocol fixes that.) The plain panel replaces it.

---

## 4. Why the raw disk‑speed test was removed

Earlier versions showed a low‑level **disk‑speed** test (sequential/random read/write MB/s and IOPS via
unbuffered Win32 I/O). It was **removed from the suite** because it's a poor Dev Drive test:

- On most PCs the system drive and the Dev Drive live on the **same physical disk**, so raw throughput is
  often ~1.0× — the test *undersells* the Dev Drive.
- Raw numbers are noisy run‑to‑run.
- The Dev Drive's real benefit is **asynchronous antivirus scanning**, which only shows up in real
  file‑churn (the builds above), not in raw MB/s.

The suite shows a calm one‑line note explaining the absence. The engine itself (`DiskBenchmark`,
`SpeedTestService`) still exists in the `DevDriveCore` library with its tests — it's simply **not wired**
into the UI.

---

## 5. How to read the numbers — what drives them, and the caveats

The single biggest factor is usually **not** raw disk speed — it's **whether your working files are
scanned by antivirus synchronously**.

A Dev Drive's advantage comes from Microsoft Defender **performance mode**: trusted Dev Drives are
scanned **asynchronously**, off the hot path. When that's in effect (*"On (async)"*), many‑small‑file
operations (restores, builds, clones) get faster. When performance mode is **off** (*synchronous*
scanning), the Dev Drive is scanned like any other volume and the advantage is **muted**, no matter how
large the fixture is — so a ~1.0× result can be an honest finding, not a bug.

### The mechanism

Writing and first‑reading **many small files** (a package restore, a working‑tree checkout, an `obj/`
tree) is where Defender's synchronous scan tax lands on `C:` (NTFS): each file write/first‑access is
scanned **inline**, before the tool can proceed. On a trusted Dev Drive with performance mode on, that
scanning happens **asynchronously**, so the build doesn't wait on it. The more small‑file write/scan
work a step does, the larger the gap.

### Indicative magnitudes — *measured on one dev machine, indicative not guaranteed*

These are **cold, symmetric** results measured on a single developer box (everything on the respective
drive, fresh per‑drive cache each run). **They are an illustration of direction and rough magnitude, not
a promise** — your numbers will differ with hardware, AV configuration, and what each step actually does:

| Step | Cold, symmetric ratio (Dev Drive vs C:) | Why |
| --- | --- | --- |
| Package‑cache **populate** (pure small‑file writes) | **~3×** | almost entirely write+scan I/O — the best case |
| **npm ci** (install) | **~1.45×** | install is I/O‑heavy (lots of small files) |
| **dotnet restore** (restore only) | **~1.27×** | restore is I/O‑heavy, but less extreme |
| **cargo build** (compile) | **~1.08×** | compile is **CPU‑bound**, so the FS benefit is small |

> **An honesty note on the app's `dotnet` row.** The numbers above for `dotnet` are for **restore**
> (pure I/O). The app's **`dotnet build`** row times the **compile** (after the untimed re‑restore), which
> is partly **CPU‑bound**, so its in‑app ratio is **more modest** than the ~1.27× restore figure — closer
> in spirit to the cargo case. That's expected, and we'd rather show the honest compile number than time
> the restore to flatter the result.

### The caveats — stated plainly

1. **Two variables, not one.** The Dev‑Drive run is **ReFS + Dev Drive performance mode (async
   Defender)**; the `C:` run is **NTFS + synchronous Defender**. Those are **two** differences (filesystem
   *and* scan mode), and on a single physical disk you **cannot cleanly separate** them. We attribute the
   gap mostly to async scanning because that's the dominant effect in small‑file work, but the filesystem
   difference is part of the measurement too — we don't claim to have isolated it.
2. **Same physical disk.** On this machine (and most), `C:` and the Dev Drive `G:` are the **same physical
   disk** — so this is *not* a fast‑SSD‑vs‑slow‑HDD result. If your drives are on **different** physical
   media, that becomes yet another variable.
3. **The gain scales with how I/O‑bound the work is.** Small‑file write/scan‑heavy steps (installs,
   restores, cache populates) benefit most; **compile‑CPU‑bound** builds (cargo, and to a lesser extent
   the `dotnet` compile) benefit least. A ~1.0×–1.1× on a compile‑bound build is the **expected, honest**
   outcome — not a failure.
4. **It depends on performance mode actually being effective.** If Defender performance mode is **off**,
   or **overridden by group policy** (the *"managed by your organization"* case), the Dev Drive is scanned
   synchronously and the advantage shrinks or disappears — regardless of fixture size.

The app detects this state up front (read‑only) and labels it honestly — including the **"managed by your
organization"** case, where group policy controls async scanning and there's nothing to turn on locally.

> **A correctness note worth knowing.** *Attached* antivirus minifilters (WdFilter / MsSecFlt) are **not**
> a signal of synchronous scanning — they attach to a Dev Drive in **both** async and sync modes. The
> actual scan mode comes from Defender's performance‑mode preference (`Get-MpPreference
> PerformanceModeStatus`: **`1` = on/async, `0` = off/sync**) plus the volume's trust/policy state. The
> authoritative per‑volume source is **Windows Security → Dev Drive protection → "See volumes."** See
> [Configuration.md §2](Configuration.md).

Other honest factors: your hardware, what's installed, and whether the system drive and Dev Drive share
the same physical disk.

---

## 6. Pre‑flight context (read‑only)

Before a run, `PreflightProbe` captures read‑only context so a result is interpretable: Defender
performance mode, Dev Drive trust state + attached filters (`fsutil devdrv query`, admin — via the
read‑only *See Filters* helper), storage class (media/bus via Storage WMI), CPU/OS summary, and free
space on both drives. **This changes nothing.**

---

## 7. Safety

- **Bounded** fixtures (a generated tree + small real projects); each copied fresh onto each drive per
  iteration, and the per‑drive cache re‑copied cold each iteration.
- **Isolated** under your `%TEMP%` (seed) and per‑drive `DevDriveManagerWorkloadBench` folders.
- **Always cleaned up** in a `finally`, even on error or cancel; each timed run also deletes its own copy,
  and the per‑drive cache is removed wholesale with the bench folder at cleanup.
- **Your real caches/projects are never touched** — every fixture is the test's own throwaway clone,
  using a **per‑drive** cache directory (`CARGO_HOME` / `NUGET_PACKAGES` / npm `--cache`) that lives under
  the bench folder on the drive being measured, **never your real one** (e.g. your real npm cache stays
  put). Those env vars are set **only** in the benchmark's own child‑process environment — never on your
  machine.
- **Nothing on your machine is modified** — no settings, environment variables, partitions, or formatting.

---

## 8. Reproduce it yourself

You don't have to trust the app — run the equivalent by hand and compare a folder on `C:` to one on your
Dev Drive (e.g. `G:`). The key is to keep it **symmetric**: put the **cache on the same drive** you're
timing, and make it a **cold first build** (fresh cache copy each run). Seed the cache once with network,
then time the offline run on each drive:

```powershell
# git — clone a local repo onto each drive (no network)
Measure-Command { git clone --no-hardlinks C:\some\local\repo C:\path\clone }
Measure-Command { git clone --no-hardlinks C:\some\local\repo G:\path\clone }

# --- npm: cache ON THE DRIVE you're timing, cold each run ---
git clone https://github.com/<your>/<proj>; cd <proj>
npm install --cache C:\seed-cache            # one-time seed (network)
# C: run — cold copy of the seed cache onto C:, then offline install with the cache on C:
robocopy C:\seed-cache C:\run\cache /E >$null; Measure-Command { npm ci --offline --cache C:\run\cache }
# G: run — cold copy onto G:, offline install with the cache on G:
robocopy C:\seed-cache G:\run\cache /E >$null; Measure-Command { npm ci --offline --cache G:\run\cache }

# --- cargo: CARGO_HOME on the drive you're timing ---
git clone --branch v2.0.0 https://github.com/microsoft/edit.git
cargo fetch --manifest-path edit\Cargo.toml      # one-time seed; warms a CARGO_HOME you copy per run
# then, with CARGO_HOME pointed at a cold copy on C: (then on G:):  cargo build --offline

# --- dotnet: NUGET_PACKAGES on the drive you're timing; re-restore offline so obj points at it ---
git clone --branch rxnet-v6.1.0 https://github.com/dotnet/reactive.git   # full clone (NBGV)
# seed:   dotnet restore Rx.NET\Source\src\System.Reactive\System.Reactive.csproj   (NUGET_PACKAGES=C:\seed-nuget)
# per drive: copy the seed cache onto the drive, then (with NUGET_PACKAGES=<drive cache>):
#   dotnet restore <csproj> --source <empty-dir>    # offline, untimed — re-points obj at the drive cache
#   Measure-Command { dotnet build <csproj> -f net6.0 -c Release --no-restore }
```

Run each a few times, ignore the first, compare the medians — the same method the app uses. To see
whether Defender performance mode is muting the win on a Dev Drive `G:`:

```powershell
[int](Get-MpPreference).PerformanceModeStatus   # 1 = on (async), 0 = off (sync)
fsutil devdrv query G:                           # run as administrator; trust state + attached filters
```

…and the authoritative per‑volume view: **Windows Security → Dev Drive protection → "See volumes."**

---

## Where this lives in the code (verify it)

Everything above is implemented in the UI‑agnostic `DevDriveCore` library so it can be unit‑tested
without touching a disk or launching a real process:

| Concern | File |
| --- | --- |
| Real‑workload orchestration (cold/discard/median, tool gating, pre‑flight) | `DevDriveCore/Services/WorkloadBenchmarkService.cs` |
| Tunables (iterations, profile) | `DevDriveCore/Services/IWorkloadBenchmarkService.cs` (`WorkloadBenchmarkOptions`) |
| Workload scaffolding (bounded fixtures, cleanup, free‑space guard, **cold per‑drive cache copy**) | `DevDriveCore/Platform/WorkloadBenchmarkBase.cs` (`PopulateColdCache`) |
| The four workloads + real projects | `DevDriveCore/Platform/{GitCloneWorkload,NpmCiWorkload,DotnetBuildWorkload,CargoBuildWorkload}.cs` |
| Tool detection + catalogue | `DevDriveCore/Services/{InstalledToolDetector,InstalledToolCatalog}.cs` |
| Pre‑flight probe + perf‑mode parse | `DevDriveCore/Services/PreflightProbe.cs` |
| Effective perf‑mode verdict | `DevDriveCore/Services/PerformanceModeEvaluator.cs` |
| Removed raw disk I/O engine (library‑only, unwired) | `DevDriveCore/Platform/DiskBenchmark.cs`, `Services/SpeedTestService.cs` |

*This document reflects the current implementation. If the code and this page ever disagree, the code is
the source of truth — please file an issue.*
