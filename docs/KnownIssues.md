# Known issues

This is the self-hosting issue register from the full repository review on 2026-07-16. **Error** items
block general mutation-enabled self-hosting; **Warning** items should be resolved before broad release.
File references identify the current implementation, not a promise that the issue is isolated there.

## Open

### SH-001: UI automation is machine-specific

**Severity:** Error  
**Evidence:** `DevDriveManager.UITests/ui-tests.ps1`

The suite requires a trusted `G:` Dev Drive, a particular recovery-volume identity, Defender performance
mode state, npm state, and sections that are intentionally hidden when no Dev Drive exists. A valid
no-Dev-Drive machine produced 25 failures. Negative text searches and the partial accessibility audit can
also report false-green coverage.

**Next:** inject deterministic volume/trust/tool fixtures and split scenarios into no Dev Drive, one Dev
Drive, multiple Dev Drives, and capability-aware real-machine smoke tests.

### SH-002: Main-page refresh can detach active work

**Severity:** Error  
**Evidence:** `DevDriveManager/MainPage.xaml`, `ViewModels/MainPageViewModel.cs`,
`ViewModels/PackageCachesViewModel.cs`, `ViewModels/PerformanceSuiteViewModel.cs`

Refresh can rebuild rows while a cache move or benchmark is still running, removing progress,
cancellation, and result state from the visible ViewModels. The creation page now blocks navigation while
its disk operation is active, but the main-page operation lifetime is still unresolved.

**Next:** own operations above reloadable row collections and gate refresh/navigation on one shared busy
state.

### SH-003: VHDX creation does not finish a Dev Drive

**Severity:** Error  
**Evidence:** `DevDriveCore/Services/DevDriveCreationService.cs`,
`DevDriveManager/ViewModels/CreateDevDriveViewModel.cs`

The VHD path creates and attaches a raw virtual disk. Initialization, partition creation, drive-letter
assignment, and `Format-Volume -DevDrive` remain manual, so this path is not end-to-end self-hosting.

**Next:** add an elevated, guarded initialization/partition/format broker with the same preview,
confirmation, and read-back model as resize.

### SH-004: Partition execution is not atomic and has no automatic recovery

**Severity:** Error  
**Evidence:** `DevDriveCore/Services/ResizePowerShellScript.cs`,
`DevDriveManager.FilterProbe/Program.cs`

`Resize-Partition`, `New-Partition`, and `Format-Volume` are separate destructive operations. A failure
after shrink can leave a smaller source volume, unallocated space, or an unformatted partition. The app reports possible partial execution or unknown state and tells the user to inspect Disk
Management, but it cannot safely roll the layout back.

**Next:** validate interruption cases in a disposable VM and publish a reviewed recovery runbook before
enabling this outside the explicit self-hosting build.

### SH-005: Persistent reversibility state is not cross-process serialized

**Severity:** Warning  
**Evidence:** `DevDriveCore/Platform/JsonFileReversibilityStore.cs`

Writes are atomic, but two app instances can both read the old JSON and then replace it, losing one
receipt. That can remove the ability to offer Move back for a successful cache move.

**Next:** add a named per-user mutex or locked transaction around load-modify-persist and test concurrent
writers.

### SH-006: Multiple Dev Drives use an arbitrary first result

**Severity:** Warning  
**Evidence:** `DevDriveManager/ViewModels/MainPageViewModel.cs`

Cache targets, benchmarks, health, and trust use the first detected Dev Drive. A user with multiple Dev
Drives cannot select or verify the mutation target.

**Next:** add an explicit selected Dev Drive and include its identity in every preview and confirmation.

### SH-007: Drive health is always labelled Healthy

**Severity:** Warning  
**Evidence:** `DevDriveManager/ViewModels/DriveHealthViewModel.cs`, `DevDriveManager/MainPage.xaml`

The green **Healthy** pill is not derived from capacity, trust, filesystem, or another health signal.

**Next:** define and compute health states, or relabel the indicator as **Detected**/**Active**.

### SH-008: The package can install on unsupported Windows versions

**Severity:** Warning  
**Evidence:** `DevDriveManager/DevDriveManager.csproj`, `DevDriveManager/Package.appxmanifest`

The minimum is Windows 10 build 17763 even though Dev Drive capabilities require newer Windows 11. The
Create action has no runtime capability gate.

**Next:** raise the package minimum or show an unsupported-state surface and disable unavailable actions.

### SH-009: Cache enumeration does not exclude reparse points

**Severity:** Warning  
**Evidence:** `DevDriveCore/Platform/SystemFileSystem.cs`,
`DevDriveCore/Services/PackageCacheMover.cs`

Recursive enumeration can enter junctions/symbolic links. A cache can therefore pull external trees into
the copy, loop through a cyclic link, or make verification and cleanup unexpectedly large.

**Next:** implement explicit recursion that skips reparse-point directories and test junction, symlink,
and loop cases.

### SH-010: Package-cache discovery failures are not surfaced

**Severity:** Warning  
**Evidence:** `DevDriveManager/ViewModels/PackageCachesViewModel.cs`

Several detection, sizing, and move-back tasks are discarded. Exceptions can leave empty, stale, or
permanently scanning UI without a retry route.

**Next:** make initialization awaitable, cancel superseded scans, and expose error/retry state.

### SH-011: Mapped cache paths are under-validated

**Severity:** Warning  
**Evidence:** `DevDriveManager/ViewModels/PackageCacheRowViewModel.cs`, `DevDriveManager/MainPage.xaml`

Map path accepts nonblank relative paths and performs synchronous `Directory.Exists` checks while the
user types, which can block the UI on unavailable network paths.

**Next:** require a normalized fully qualified path and debounce asynchronous existence validation.

### SH-012: Elevated helper coverage is not end-to-end

**Severity:** Warning  
**Evidence:** `DevDriveManager.FilterProbe/Program.cs`,
`DevDriveCore.Tests/ResizePowerShellScriptTests.cs`

The generated privileged scripts and pure guard are unit-tested, but argument dispatch, output-path
hardening, elevation, PowerShell execution, and partial-failure reporting are not exercised together.

**Next:** add helper-level dry-run tests and a disposable-VM integration lane.

### SH-013: No localization infrastructure

**Severity:** Warning  
**Evidence:** `DevDriveManager/**/*.xaml`, `DevDriveManager/**/*.cs`,
`DevDriveManager/Package.appxmanifest`

User-facing strings and package metadata are hard-coded; there are no `.resw` resources or `x:Uid`
bindings.

**Next:** add `Strings/en-US/Resources.resw`, `x:Uid`, `ResourceLoader`, and `ms-resource:` manifest
values before localization.

### SH-014: Trimming is disabled

**Severity:** Note  
**Evidence:** `DevDriveManager/DevDriveManager.csproj`

Release trimming was disabled because core persistence and elevation contracts still use
reflection-based `System.Text.Json`. This preserves runtime correctness but increases package size.

**Next:** add source-generated JSON contexts for all persisted/elevation types, test a published Release,
then reconsider trimming.

## Resolved in the self-hosting readiness branch

| Item | Resolution |
| --- | --- |
| ARM64 build selected `win-x86` from MSBuild process bitness | RID now follows the requested `Platform`. |
| Bare `dotnet test` could silently build without running tests | Invalid CLI configurations are no longer skipped; the suite executes. |
| UI safety assertion recorded a failure and continued into real Move | The script now exits with code `2` before all tests when safe mode is absent. |
| VHD Browse returned an already-created file rejected by provisioning | Browse now selects a folder and composes a non-existing `.vhdx` path. |
| Failed/read-only creation outcomes exposed post-create actions | Preview, failure, partial execution, and usable-Dev-Drive states now have separate actions. |
| Users could leave the creation page during disk mutation | Back and form interaction are blocked while creation/resize is busy. |
| Release trimming broke reflection-based JSON paths | Trimming is disabled until source-generated contracts are added. |
| Elevated helper could prompt for a separately installed .NET runtime | The app now publishes with the .NET and Windows App SDK runtimes bundled; the helper resolves the bundled runtime from the package directory. |
| Lost or malformed execute output could report that nothing changed | Nonzero helper exits, missing output, and malformed execute results now report state unknown and require disk inspection before retry. |
| Live preflight failures were reported as partial disk mutations | The script flushes a mutation-start marker immediately before `Resize-Partition`, distinguishing safe preflight rejection from possible partial execution. |
| Over-the-shoulder elevation rejected the caller's temp output root | The broker trims `%TEMP%`'s trailing separator before quoting `--allowed-root`, so Windows argument parsing preserves the root path. |
| Resize gate had no supported activation path | `/p:EnableRealResizeExecute=true` emits the AppContext switch for deliberate self-hosting builds. |
| Resize used stale preview values at execution | The helper re-queries live disk identity, filesystem, supported size, free letter, and reclaimable bytes immediately before shrink. |
