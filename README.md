# Dev Drive Manager

A Windows desktop app (WinUI 3 / Windows App SDK) that helps developers discover, benchmark, and
plan a move to a [Dev Drive](https://learn.microsoft.com/windows/dev-drive/) — a ReFS volume tuned
for developer workloads (package caches, source trees, build output).

> **Safety-first design.** Changes require an explicit confirmation; destructive resize also requires a
> live read-only preview and a second confirmation. Package-cache moves stay per-user and reversible.
> Storage creation crosses a narrow UAC-elevated helper that revalidates live state immediately before
> mutation and reports partial or unknown outcomes instead of claiming success.

---

## Documentation

In-depth docs live in [`docs/`](docs/README.md):

| Doc | What it covers |
|---|---|
| [docs/README.md](docs/README.md) | Index, architecture, and the safety model. |
| [docs/Configuration.md](docs/Configuration.md) | Every setting/option and **why** (Defender performance mode, benchmark tunables, the safe-mutation seam, reversible-state storage, the cache catalogue). |
| [docs/CreatingADevDrive.md](docs/CreatingADevDrive.md) | Creating a Dev Drive — VHDX vs. resize, every option, and what is **real vs. simulated**. |
| [docs/PackageCacheMoves.md](docs/PackageCacheMoves.md) | Moving package caches and the **documented fallbacks** for every failure path (rollback, idempotency, Move back). |
| [docs/SpeedTest.md](docs/SpeedTest.md) | The performance tests — what each test measures, why, and how missing tools are flagged. |
| [docs/Testing.md](docs/Testing.md) | Test-machine setup, package/tool detection, safe UI automation, and partition self-hosting. |
| [docs/KnownIssues.md](docs/KnownIssues.md) | Confirmed self-hosting and release-readiness issues. |

---

## Solution layout

| Project | Kind | TFM | Notes |
|---|---|---|---|
| `DevDriveManager` | WinUI 3 app (MVVM) | `net10.0-windows10.0.26100.0` | The UI. Composes services via their `CreateDefault()` factories. |
| `DevDriveCore` | Class library | `net10.0-windows10.0.26100.0` | All logic. **No WinUI types.** Interface-driven so it is fully unit-testable. |
| `DevDriveCore.Tests` | MSTest unit tests | `net10.0-windows10.0.26100.0` | Mocks/fakes + temp-dir sandboxes. Never mutates the real machine. |
| `DevDriveManager.UITests` | PowerShell UI test script | — | `ui-tests.ps1` (script-driven, no csproj). |

`DevDriveCore` follows a consistent pattern: each service takes its dependencies as **interfaces**
(constructor-injected, null-checked) and exposes a static **`CreateDefault()`** factory that wires
the real platform implementations. Pure decision logic is exposed as `public static` methods so it
can be unit-tested directly.

---

## Capability state

The codebase has two tiers of capability: **read-only/scratch** work and **guarded mutations**. Package
cache mutations are per-user and reversible; storage mutations require UAC and have operation-specific
recovery limits.

### ✅ REAL — wired into the app and shipping

These surfaces default to read-only detection or throwaway scratch work.

- **Volume & Dev Drive detection** — `DevDriveService` reads volumes and decodes the Dev Drive /
  trusted flags. Read-only.
- **Real-world build benchmarks** — `WorkloadBenchmarkService` times genuine developer workloads
  (`git clone`, `npm ci`, `dotnet build`, `cargo build`) on C: vs the Dev Drive against real,
  Microsoft-owned fixtures (e.g. `npm ci` of **microsoft/vscode-eslint**'s committed lockfile). Each run
  is a cold first build with the package cache, source, and output all on the drive under test —
  bounded, offline, and self-cleaning. Only those four tools have a workload, so only they ever show a
  measured number; every other ecosystem is honestly marked "no benchmark yet" — never a fabricated one.
- **Per-ecosystem package caches** — `PackageCacheService` + the per-ecosystem cards detect known tool
  caches (14 tools across Node, .NET, Rust, Python, Java, Go, C++, Dart) and show where each lives, then
  offer a reversible **move** onto the Dev Drive — or **Map path** / **Move &amp; remap** for a tool
  whose cache wasn't auto-detected. Read-only until you confirm.

### 🔁 GUARDED mutations — wired into the app

Each mutation runs only after explicit confirmation. Storage operations use the bundled self-contained
helper under UAC; no separately installed runtime, StorageDsc, or PowerShell module is required. Under
the UI-test seam `DDM_UITEST_SAFE_MUTATIONS=1`
the ViewModel drives the *same* Core coordinator over **safe fakes**, so the automated suite exercises
the full **Move → progress → Move-back** (and create) flow WITHOUT touching a real cache, environment
variable, or disk. Every engine is interface-driven with a `CreateDefault()` factory, and all
file/environment/VHD access goes through abstractions so unit tests run against in-memory fakes or
throwaway temp directories.

| Engine | Interface | What it does | Recovery |
|---|---|---|---|
| `PackageCacheMover` | `IPackageCacheMover` | Creates the target dir, copies + SHA-256-verifies the cache (with progress), repoints the per-user env var (`IEnvironmentWriter`), records reversibility. | ✅ restore env (+ optional move-back) |
| `ElevatedVhdProvisioner` | `IVhdProvisioner` | Creates/attaches a new VHDX, binds the exact image to its disk, initializes GPT, partitions, formats with `Format-Volume -DevDrive`, and verifies ReFS/Dev Drive state. | Core recovery path can detach + delete; recovery UI is not yet exposed. |
| `VolumeResizer` | `IVolumeResizer` | Runs a live read-only preview, then after a second confirmation shrinks, partitions, formats, and reads back the new Dev Drive through the elevated helper. | No; separate Storage operations are not atomic. |

Wiring: `PackageCachesViewModel` → `MutationComposition.CreatePackageCacheMoveCoordinator()`;
`CreateDevDriveViewModel` → `MutationComposition.CreateDevDriveCreationService()`. Supporting
abstractions: `IFileSystem`, `IEnvironmentWriter`, `IReversibilityStore`, `IPathProbe`, `INativeVhdApi`,
`IElevatedVhdBroker`, and `IElevatedResizeBroker`.
Reversibility persists via `JsonFileReversibilityStore` (or in-process `InMemoryReversibilityStore`).
Read-only tool detection (`InstalledToolDetector` / `IInstalledToolDetector`) probes `--version` and
PATH (`IProcessRunner` / `IPathProbe`).

---

## Build / run / test

Prerequisites: .NET SDK 10, Windows App SDK workload, Developer Mode on. (WinUI apps must target
**x64** or **ARM64** — never AnyCPU.)

These are development prerequisites only. Deployment is self-contained: the packaged app bundles both
the .NET runtime and Windows App SDK runtime, so an end user does not install either runtime separately.

```powershell
# Build the whole solution (Debug)
dotnet build DevDriveManager.slnx -c Debug

# Run the app (packaged WinUI app — never launch the raw .exe).
# In Visual Studio: open DevDriveManager.slnx and press F5.
# Or with the Windows App SDK CLI (winget install Microsoft.WinAppCLI):
winapp run DevDriveManager\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\AppX

# Run the unit tests (fast; AnyCPU is fine for the library + tests)
dotnet test DevDriveCore.Tests\DevDriveCore.Tests.csproj -c Debug
```

Create release packages from a dedicated publish directory, not a previously generated `bin\...\AppX`
loose layout:

```powershell
$platform = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }
$rid = if ($platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$publish = Join-Path $PWD "artifacts\publish\$rid"

dotnet publish .\DevDriveManager\DevDriveManager.csproj `
    -c Release -p:Platform=$platform -r $rid --self-contained true -o $publish

winapp package $publish `
    --manifest .\DevDriveManager\Package.appxmanifest `
    --exe DevDriveManager.exe `
    --output ".\artifacts\DevDriveManager-$platform.msix"
```

The publish output already contains both runtimes, so no runtime download or separate framework
installer is part of deployment. Add the appropriate certificate options to `winapp package` for signed
distribution.

The unit-test suite includes `[TestCategory("Integration")]` tests that exercise the real
`SystemFileSystem` / `DiskBenchmark` against unique temp directories (always cleaned up). They never
touch user data. To skip them:

```powershell
dotnet test DevDriveCore.Tests\DevDriveCore.Tests.csproj --filter "TestCategory!=Integration"
```

---

For real storage self-hosting, follow
[docs/Testing.md](docs/Testing.md#7-real-storage-self-hosting) and use a disposable VM.

---

## Current guarantees and limits

- The solution builds clean in the validated native platform and the core suite runs without machine
  mutation.
- Package-cache mutations are preview → explicit confirm → per-user (no admin) → reversible. Detection
  and benchmarks are read-only / scratch-only. Disk partitioning is the documented exception: it
  requires admin and is not automatically reversible.
- Real resize execution is exposed only after a passing live preview; a second confirmation, UAC,
  execution authorization, and in-helper live safety checks still apply.
- **Tests never touch your data:** every test runs against in-memory fakes or unique throwaway temp
  directories (all VHD tests mock `INativeVhdApi`); the UI suite uses a safe-mutation seam.
- The current UI suite is machine-profile-specific; general self-hosting blockers are tracked in
  [docs/KnownIssues.md](docs/KnownIssues.md).

---

## License

[MIT](LICENSE) © 2026 Clint Rutkas.
