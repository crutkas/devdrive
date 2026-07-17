# Testing and self-hosting

This guide defines the machine profiles and commands used to validate DevDriveManager. Keep the
portable, safe test lane separate from real machine mutations.

## 1. Test lanes

| Lane | Machine | Mutates the machine? | Purpose |
| --- | --- | --- | --- |
| Build + unit | Any supported x64/ARM64 Windows development machine | No | Compile, analyzers, core logic, fake mutation engines |
| UI safe-mutation | Dedicated test account or VM | Only read-only detection and benchmark scratch data; mutation engines are faked | UI automation, confirmations, move/move-back state |
| Detection matrix | Dedicated test machine with developer tools and populated caches | Tool installers and normal package restores only | Verify tool and package-cache discovery |
| Real storage | Disposable VM with a checkpoint; add a secondary fixed disk for resize | **Yes: creates/attaches/formats a VHDX or shrinks/partitions/formats a volume** | Self-host both complete creation paths |

Never run the real-partition lane on a machine that contains the only copy of important data.

## 2. Base machine

Use Windows 11 24H2 (build 26100 or later) to match the app target. Enable Developer Mode and install:

- Visual Studio or Build Tools with MSBuild and the Windows App SDK prerequisites.
- .NET SDK 10.
- WinApp CLI (`winapp`) for packaged launch and UI automation.
- Git.

Verify the shell can resolve the required commands:

```powershell
Get-Command dotnet, git, winapp
dotnet --info
git --version
winapp --version
```

Choose the native platform and run the build and core suite:

```powershell
$platform = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" }

dotnet build .\DevDriveManager.slnx -c Debug -p:Platform=$platform
dotnet test .\DevDriveCore.Tests\DevDriveCore.Tests.csproj -c Debug -p:Platform=$platform
```

A bare `dotnet test` also executes the suite, but specifying the platform keeps its output aligned with
the packaged app build.

## 3. Tool detection profile

The app runs these exact read-only probes. It does not enforce minimum versions; use current stable
releases and ensure each executable is on `PATH`.

| Display name | Executable | Probe | Workload |
| --- | --- | --- | --- |
| git | `git` | `git --version` | Offline local `git clone` |
| node | `node` | `node --version` | None |
| npm | `npm` | `npm --version` | `npm ci` |
| pnpm | `pnpm` | `pnpm --version` | None |
| yarn | `yarn` | `yarn --version` | None |
| dotnet | `dotnet` | `dotnet --version` | `dotnet build` |
| cargo | `cargo` | `cargo --version` | `cargo build` |
| rustc | `rustc` | `rustc --version` | None |
| python | `python` | `python --version` | None |
| pip | `pip` | `pip --version` | None |
| java | `java` | `java -version` | None |
| maven | `mvn` | `mvn --version` | None |
| gradle | `gradle` | `gradle --version` | None |
| go | `go` | `go version` | None |

Check the complete profile in one shell:

```powershell
git --version
node --version
npm --version
pnpm --version
yarn --version
dotnet --version
cargo --version
rustc --version
python --version
pip --version
java -version
mvn --version
gradle --version
go version
```

## 4. Package-cache detection profile

Cache detection is directory-based. Installing a tool is not enough: run one normal restore/install so
its cache directory exists. Environment-variable overrides take precedence over the listed default.

| Cache | Relocation variable | Default on Windows | Representative population action |
| --- | --- | --- | --- |
| npm | `npm_config_cache` | `%LocalAppData%\npm-cache` | `npm cache verify` or `npm install` in a fixture |
| NuGet global packages | `NUGET_PACKAGES` | `%UserProfile%\.nuget\packages` | `dotnet restore` |
| pip | `PIP_CACHE_DIR` | `%LocalAppData%\pip\Cache` | `python -m pip download requests` |
| uv | `UV_CACHE_DIR` | `%LocalAppData%\uv\cache` | Run `uv sync` in a fixture |
| Poetry | `POETRY_CACHE_DIR` | `%LocalAppData%\pypoetry\Cache` | Run `poetry install` in a fixture |
| Cargo | `CARGO_HOME` | `%UserProfile%\.cargo` | `cargo fetch` |
| vcpkg | `VCPKG_DEFAULT_BINARY_CACHE` | `%LocalAppData%\vcpkg\archives` | Install a small port |
| Gradle | `GRADLE_USER_HOME` | `%UserProfile%\.gradle` | `gradle help` in a fixture |
| Go modules | `GOMODCACHE` | `%UserProfile%\go\pkg\mod` | `go mod download` |
| Yarn | `YARN_CACHE_FOLDER` | `%LocalAppData%\Yarn\Cache` | Run `yarn install` in a fixture |
| pnpm | `PNPM_CONFIG_STORE_DIR` | `%LocalAppData%\pnpm\store` | Run `pnpm install` in a fixture |
| Bun | `BUN_INSTALL_CACHE_DIR` | `%UserProfile%\.bun\install\cache` | Run `bun install` in a fixture |
| Deno | `DENO_DIR` | `%LocalAppData%\deno` | Run the normal cache/install command for a fixture |
| Pub (Dart/Flutter) | `PUB_CACHE` | `%LocalAppData%\Pub\Cache` | Run `dart pub get` or `flutter pub get` |

After population, launch the app and compare each displayed path with the corresponding tool command or
environment variable. Test three states for at least npm, NuGet, and Cargo:

1. Default path on the system drive.
2. A custom path set by the relocation variable.
3. A path on the selected Dev Drive.

The current UI automation script assumes a specific `G:` Dev Drive and several machine-specific
properties. It is not yet a portable substitute for this matrix; see
[KnownIssues.md](KnownIssues.md#sh-001-ui-automation-is-machine-specific).

## 5. Workload benchmark profile

Four workloads exist:

- `git clone`: generated local bare repository; fully offline.
- `npm ci`: `microsoft/vscode-eslint` at `release/3.0.24`.
- `dotnet build`: `dotnet/reactive` at `rxnet-v6.1.0`.
- `cargo build`: `microsoft/edit` at `v2.0.0`.

The last three need GitHub/package-network access during preparation. Measured runs use copied,
per-drive caches and run offline. Without a Dev Drive, the page runs a system-drive-only baseline and
shows the measured wall-clock times without a comparison ratio. When a Dev Drive is available, the same
workloads run on both drives. Allow several GiB of free scratch space on every measured drive and do not
run unrelated disk-heavy work during a benchmark.

The benchmark page separately probes the required command-line executables (`git`, `npm`, `dotnet`, and
`cargo`). A missing executable disables that workload and shows why; **Run all tests** skips unavailable
workloads. Package-cache discovery is intentionally separate: a tool can be installed before it has
created a cache, and a stale cache can remain after a tool is removed.

## 6. Safe UI automation

The UI suite can confirm mutation dialogs, so safe mode is mandatory. Set the seam at **user scope
before launching** the packaged app; process-scoped variables are not reliably inherited by packaged
activation. The script exits with code `2` before any test if the visible safe-mode indicator is absent.

In the launch terminal:

```powershell
$name = "DDM_UITEST_SAFE_MUTATIONS"
$previous = [Environment]::GetEnvironmentVariable($name, "User")
[Environment]::SetEnvironmentVariable($name, "1", "User")

# Launch after setting the variable and note the app PID printed by winapp.
winapp run <path-to-AppX>
```

While the app is running, use a second terminal:

```powershell
.\DevDriveManager.UITests\ui-tests.ps1 -AppPid <pid>
```

After the run, restore the user-scoped value in the launch terminal:

```powershell
[Environment]::SetEnvironmentVariable($name, $previous, "User")
```

Confirm that **Safe-mutation test mode** is visible before invoking Move, Move back, VHD creation, or
resize controls. Safe mode swaps package moves, VHD provisioning, and volume resizing for in-memory
fakes.

## 7. Real storage self-hosting

The normal self-contained build supports complete VHDX creation and resize execution. This does not
bypass safeguards: VHDX creation requires explicit confirmation and UAC; resize additionally requires a
successful elevated read-only preview and a second destructive confirmation. The helper rechecks live
identity and safety conditions after elevation and immediately before mutation.

Use this machine profile:

1. Disposable Windows 11 24H2 VM with a checkpoint.
2. A secondary fixed virtual disk containing an NTFS test volume with at least 100 GiB free.
3. At least one unused drive letter.
4. Administrator credentials and no active disk-heavy workload.
5. A backup or checkpoint verified before execution.

### VHDX creation

1. Select **Create a new VHDX**, a new path on the VM, an unused letter, and at least 50 GiB.
2. Review the create/attach/identity-check/initialize/partition/format/read-back steps.
3. Confirm and approve UAC.
4. Verify the result from an elevated shell:

   ```powershell
   Get-DiskImage -ImagePath C:\DevDrives\DevDrive.vhdx | Get-Disk
   Get-Partition -DriveLetter D
   Get-Volume -DriveLetter D
   fsutil devdrv query D:
   ```

5. Reboot and verify the VHDX reattaches and the Dev Drive remains usable.
6. Restore the VM checkpoint after testing.

If creation reports a confirmed rollback, verify that the VHDX path and requested letter are absent. If
it reports unknown state, do not retry: inspect both `Get-DiskImage` and Disk Management first.

### Resize

Exercise only against the secondary test volume:

1. Select **Resize an existing volume** and choose the secondary test volume.
2. Request at least 50 GiB and run the read-only preview.
3. Verify the source, target letter, aligned size, and three displayed commands.
4. Select **Apply resize**, read the destructive confirmation, and approve UAC.
5. Verify the new volume with `Get-Volume`, `Get-Partition`, and `fsutil devdrv query <letter>:` from an
   elevated shell.
6. Reboot and verify the source and new Dev Drive still mount correctly.

If execution reports a possible partial mutation or unknown state, **do not retry**. Open Disk
Management, inspect the source size, unallocated space, and target partition, then restore the VM
checkpoint or follow a reviewed recovery plan. Shrink, partition creation, and formatting are not one
atomic operation.

## 8. Runtime-free install check

The shipped package is self-contained. Before release, install it on a clean supported VM that has
neither the .NET Desktop Runtime nor Windows App SDK runtime installed. Do not install either runtime
when prompted.

Verify all executable boundaries:

1. Launch the packaged app and confirm the main window renders.
2. Select **See filters**, approve UAC, and confirm the elevated helper returns a result without a runtime
   installation prompt.
3. Run a resize feasibility preview and confirm the elevated helper returns a result.
4. On a checkpointed disposable VM, create a 50 GiB dynamic VHDX and verify the helper completes without
   a runtime or module installation prompt.
5. In **Apps > Installed apps**, confirm no test step installed a .NET runtime, Windows App SDK runtime,
   StorageDsc, or another PowerShell module.

The build must keep `SelfContained=true`, `PublishSelfContained=true`, and
`WindowsAppSDKSelfContained=true`. Create the MSIX from a dedicated `dotnet publish` directory as shown
in the root README; do not package a stale `bin\...\AppX` loose layout.

## 9. Release gate

Before widening self-hosting beyond the disposable-VM lane:

- Make the UI suite deterministic across no-Dev-Drive, one-Dev-Drive, and multiple-Dev-Drive states.
- Add automated end-to-end elevated-helper dry runs for both VHDX and resize verbs.
- Validate interrupted/partial partition outcomes and publish a recovery runbook.
- Resolve all **Error** items in [KnownIssues.md](KnownIssues.md).
