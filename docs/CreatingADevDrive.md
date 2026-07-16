# Creating a Dev Drive — the two code paths and every option

Creating a Dev Drive has **two distinct code paths**, chosen by the **Source** dropdown on the Create
page:

1. **New VHDX** — create a virtual disk file and attach it as a new drive.
2. **Resize an existing volume** — shrink a volume (e.g. `C:`) and carve a new partition from the freed
   space.

They behave very differently with respect to what is *real* vs. *simulated* — read §3 carefully.

- ViewModel: `DevDriveManager/ViewModels/CreateDevDriveViewModel.cs`
- Engine: `DevDriveCore/Services/DevDriveCreationService.cs`
- Size math: `DevDriveCore/Services/DevDriveSizeMath.cs`
- VHDX provisioning: `DevDriveCore/Platform/VhdProvisioner.cs` (behind `IVhdProvisioner`)

---

## 1. Options on the Create page

| Option | Applies to | Default | Notes |
| --- | --- | --- | --- |
| **Source** | both | New VHDX | `0` = new VHDX, `1` = resize an existing volume (`SourceIndex`). |
| **Name / label** | both | `DevDrive` | The volume label. |
| **Drive letter** | both | `D:` | Chosen from `AvailableDriveLetters` (letters already in use are excluded). |
| **Size** | both | — | See §2. Minimum **50 GiB**; maximum = the source's free/shrinkable space. |
| **VHDX file path** | VHDX | — | Where the `.vhdx` is written. |
| **VHDX type** | VHDX | Dynamically expanding | `0` = dynamically expanding, `1` = fixed size (`VhdTypeIndex`). |
| **Source volume** | Resize | — | Chosen from `AvailableSourceVolumes` — NTFS/ReFS volumes with at least 50 GB free that can be shrunk. |

### The size control

The size is one value — `SelectedBytes` — kept in sync across a **Slider**, a **NumberBox**, a **GB/MB
unit** dropdown, and a draggable **disk‑bar**. All of the conversions and clamps are pure functions in
`DevDriveSizeMath` (so they're unit‑tested without any XAML):

- Sizes are **binary** (1024‑based) to match Windows' "GB"/"MB" labelling.
- The hard **minimum is 50 GiB** (`MinimumSizeBytes`) — this mirrors the platform's
  `c_minimumSizeForDevVolumeInBytes = 50 << 30`. A request below it drives a *"Minimum 50 GB"* message.
- The **maximum** selectable size equals the source's free/shrinkable space; a request above it drives a
  *"Not enough space"* message.
- The disk‑bar shows three segments — **used + protected**, **remaining free/shrinkable**, and the
  carved **Dev Drive** chunk — with the drag handle on the boundary between *remaining* and *Dev Drive*.
  Dragging left grows the Dev Drive; dragging right shrinks it.

---

## 2. The flow: configure → preview → **Confirm**

Nothing is created while you are editing. You configure the options, the app shows a **preview**, and a
mutating action only happens behind an **explicit Confirm**. Cancel/Back always returns to Dev Drive
management without side effects.

---

## 3. What is real vs. simulated  *(the important part)*

### New VHDX — **real create + attach** (format is a separate, pending step)

`DevDriveCreationService.CreateVhdDevDriveAsync`:

- Builds a `VhdProvisionPlan` and runs it through the **real** `IVhdProvisioner`, which **creates and
  attaches** the `.vhdx` using public Windows VirtualDisk APIs. This genuinely happens once you confirm.
- The provisioning is **reversible** — the result carries a `ReversibilityId`.
- **The Dev Drive *format* is not yet wired.** Marking a volume as a trusted Dev Drive requires
  `Format-Volume -DevDrive` (the underlying `FMIFS_FORMAT_DEV_VOLUME` flag is internal, so this is the
  only public path) **and administrator**. The engine therefore reports `FormatPending = true` and a
  summary telling you to finish by formatting the new letter as a ReFS Dev Drive. So after a confirmed
  VHDX creation you have a real attached virtual disk, but the *"make it a Dev Drive"* format is a
  deliberate, separate, admin‑gated step that this build does not perform for you.

Guards: the plan must be a VHDX plan, must have a file path, and must be **≥ 50 GiB**, or the call
throws before doing anything.

### Resize an existing volume — gated, **off by default**

`DevDriveCreationService.SimulateResize` builds a `DevDriveResizeSimulation` preview, and the app can run
a **real, read-only feasibility check** for it.

- **Preview is real and read‑only.** When the elevated helper is available, the app runs
  `IVolumeResizer.PreviewAsync` → the broker's `--whatif` mode, which queries the *actual* reclaimable
  space (`Get-PartitionSupportedSize`) and runs the safety guards — it changes nothing. If the helper or
  UAC is unavailable it falls back to the pure‑computation `SimulateResize`.
- **Execute is real but OFF BY DEFAULT.** The destructive shrink→repartition→format path is gated behind
  `ResizeFeatureGate.EnableRealResizeExecute` (an `AppContext` switch that defaults to **false**), an
  explicit confirm, **UAC elevation**, and in‑helper guards (`ResizeGuard` refuses system / EFI / recovery
  / removable / RAW volumes and enforces the 50 GiB minimum + alignment). A deliberate self-hosting build
  enables the switch with `-p:EnableRealResizeExecute=true`; normal builds remain preview-only.
- **Execution re-checks live state.** Immediately before shrink, the elevated helper re-queries the source
  partition, disk identity, filesystem, supported minimum size, reclaimable bytes, and target drive
  letter. If they changed after preview, execution stops before `Resize-Partition`.
- Repartitioning your system drive is **destructive and not trivially reversible** — the confirm copy
  says so plainly.

The execute path uses only **public Storage cmdlets** (no internal engines):

1. Shrink the source (e.g. `C:`) with `Resize-Partition`.
2. Carve a new partition of the freed size with `New-Partition` and assign the chosen drive letter.
3. Format the new letter as ReFS with the Dev Drive flag (`Format-Volume -DevDrive`; requires admin).

These are separate operations, not one atomic transaction. If partition creation or formatting fails
after shrink, inspect Disk Management and do not retry until the layout is understood. Real execution is
therefore restricted to the disposable-VM self-hosting lane in [Testing.md](Testing.md#7-real-partition-self-hosting).

---

## 4. Which Windows APIs are public (and which aren't)

A summary of which Windows APIs are public vs. internal:

| Operation | Status |
| --- | --- |
| Create + attach a VHDX | **Public** (Windows VirtualDisk APIs) — used by the VHDX path. |
| Partitioning / shrink (`Resize-Partition`, `New-Partition`) | **Public** — used by the gated, default-off resize execute path. |
| Detect a Dev Drive (`FSCTL_QUERY_PERSISTENT_VOLUME_STATE`) | **Public** — used everywhere for detection. |
| Format a volume *as a Dev Drive* (`FMIFS_FORMAT_DEV_VOLUME`) | **Internal** — so the only public way to do it is `Format-Volume -DevDrive` (admin). This app uses only that public cmdlet. |

---

## 5. Safety summary for creation

- Editing options changes nothing; a mutation requires an explicit **Confirm**.
- **VHDX:** create + attach is real and **reversible**; the Dev Drive *format* is a separate admin step
  the app does not perform (it reports `FormatPending`).
- **Resize:** the **preview** is real but **read-only** (it only queries reclaimable space); the
  destructive execute is **off by default** (gated by `ResizeFeatureGate` + confirm + UAC + in-helper
  guards) — the default UI never repartitions.
- Unit tests exercise the VHDX path exclusively against a **mock** native API, so no test creates,
  attaches, or formats a real disk.
