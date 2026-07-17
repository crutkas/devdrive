# Creating a Dev Drive

The Create page offers two real Windows storage paths:

1. **New VHDX** creates a virtual disk file and turns that exact disk into a Dev Drive.
2. **Resize an existing volume** shrinks a selected partition and creates a Dev Drive in the freed space.

Neither path requires StorageDsc, WinGet Configuration, a separately installed PowerShell module, the
.NET runtime, or the Windows App SDK runtime. The app package contains the app and elevated helper;
privileged storage work uses Windows' inbox Virtual Disk APIs and Storage cmdlets.
Both paths require Windows 11 build 22621.2338 or later and verify that the inbox
`Format-Volume` command exposes its `-DevDrive` parameter before mutation.

Key implementation points:

- ViewModel: `DevDriveManager/ViewModels/CreateDevDriveViewModel.cs`
- Orchestration: `DevDriveCore/Services/DevDriveCreationService.cs`
- Elevated VHD broker: `DevDriveCore/Services/ElevatedVhdProvisioner.cs`
- Native VHD stage: `DevDriveCore/Services/VhdProvisioner.cs`
- VHD format script: `DevDriveCore/Services/VhdPowerShellScript.cs`
- Elevated helper: `DevDriveManager.FilterProbe/Program.cs`

## 1. Options

| Option | Applies to | Default | Notes |
| --- | --- | --- | --- |
| **Source** | both | New VHDX | Select VHDX creation or resize. |
| **Name / label** | both | `DevDrive` | Sanitized before it crosses the elevation boundary. |
| **Drive letter** | both | first free letter from `D:` onward | Rechecked immediately before mutation. |
| **Size** | both | up to 256 GiB, clamped to available space | Minimum is 50 GiB. A VHDX adds 128 MiB of internal GPT/alignment headroom so the partition still equals the selected size. |
| **VHDX file path** | VHDX | `C:\DevDrives\DevDrive.vhdx` | Must be a new, fully qualified `.vhdx` path outside reparse points. |
| **VHDX type** | VHDX | dynamically expanding | Fixed size preallocates the requested capacity. |
| **Source volume** | resize | system volume when eligible | Only detected NTFS/ReFS volumes with at least 50 GiB free are offered. |

`SelectedBytes` is the single size value behind the slider, number box, unit picker, and disk bar.
`DevDriveSizeMath` applies the 50 GiB platform minimum and clamps the request to the selected source.

## 2. Confirmation and elevation

Editing options changes nothing.

- VHDX creation has one explicit confirmation followed by one UAC prompt.
- Resize has one explicit **Create** confirmation showing the planned final source and target sizes,
  followed by one UAC prompt. The elevated helper verifies the live disk before making any change.
- `DDM_UITEST_SAFE_MUTATIONS=1` replaces both production engines with safe fakes.

The elevated helper accepts base64-encoded JSON plans on its command line, not mutable plan files. A
create or resize execution plan must also carry an authorization flag set by the production service.

## 3. New VHDX: complete transaction

After confirmation, `ElevatedVhdProvisioner` writes a per-user recovery receipt before invoking the
bundled helper. The helper then performs these stages:

1. Verify Dev Drive formatting support, then refuse an existing file, a non-`.vhdx` path, a path below
   a junction/symlink, an undersized request, or a target letter outside `D:` through `Z:`.
2. Create and permanently attach the new VHDX with `CreateVirtualDisk` and `AttachVirtualDisk`.
3. Resolve the native physical disk number.
4. Resolve the same backing file through `Get-DiskImage`, then require that it maps to that exact disk
   number and is still RAW, empty, online, writable, and the expected size.
5. Recheck the target drive letter.
6. Run `Initialize-Disk -PartitionStyle GPT`.
7. Run `New-Partition -Size <selected size>` with the selected letter; the VHD container has separate
   metadata/alignment headroom.
8. Run `Format-Volume -DevDrive -FileSystem ReFS`.
9. Read back the image, disk, partition, filesystem, size, and `fsutil devdrv query` result before
   reporting success.

The helper imports the inbox Storage module from its absolute System32 path and resolves `fsutil.exe`
from `Environment.SystemDirectory`; it does not trust `PATH` or a user PowerShell module location.

### Failure behavior

The VHDX is new and isolated from existing partitions, so a confirmed failure after attachment can be
rolled back safely by detaching and deleting that new VHDX. The helper reports that rollback explicitly.

If a timeout, process interruption, output failure, or cleanup failure makes the state uncertain, the
app does not claim success or "nothing changed." It retains the recovery receipt and tells the user to
inspect Disk Management before retrying. A native create failure never deletes a file whose ownership
was not proven.

## 4. Resize an existing volume

Resize uses the same inbox Storage module but cannot be made atomic.

### Verify and execute

After **Create**, `IVolumeResizer.VerifyAndExecuteAsync` invokes helper mode `resize --execute` once. Inside
that elevated process, the helper queries the live partition, disk, filesystem, supported minimum size,
reclaimable bytes, bus type, protected status, and used drive letters. It runs every guard and binds the
exact disk/partition identity. If verification fails, it reports the reason and changes nothing.

The helper then re-queries and compares that bound identity immediately before mutation:

1. `Resize-Partition` shrinks the selected source.
2. `New-Partition` creates the requested partition and assigns the selected letter.
3. `Format-Volume -DevDrive -FileSystem ReFS` formats it.
4. The helper verifies the final disk, partition, letter, size, ReFS filesystem, and
   `fsutil devdrv query` result before reporting success.

The helper refuses RAW/unknown disks, removable media, offline/read-only disks, protected
system/recovery/reserved partitions, unsupported filesystems, a disk/partition identity that changes
between verification and mutation, a changed aligned size or reclaimable space, and a newly occupied
target letter. `IVolumeResizer.PreviewAsync` and helper mode `resize --whatif` remain available for
read-only diagnostics, but the creation UI does not require a separate preview round trip.

These three mutation commands are separate. A failure after shrink can leave a smaller source,
unallocated space, or an unformatted partition. Do not retry an outcome marked partial or unknown until
the layout is understood. Real resize testing belongs only on the disposable profile in
[Testing.md](Testing.md#7-real-storage-self-hosting).

## 5. Public Windows surface

| Operation | Surface used |
| --- | --- |
| Create, attach, detach VHDX | `virtdisk.dll` public APIs |
| Bind backing file to disk | `Get-DiskImage` piped to `Get-Disk` |
| Initialize and partition | `Initialize-Disk`, `New-Partition` |
| Shrink an existing partition | `Get-PartitionSupportedSize`, `Resize-Partition` |
| Format as a Dev Drive | `Format-Volume -DevDrive -FileSystem ReFS` |
| Verify Dev Drive state | `fsutil devdrv query` and the existing volume detector |

`FMIFS_FORMAT_DEV_VOLUME` is internal; the app does not call it. `Format-Volume -DevDrive` is the
supported public path.

## 6. Safety summary

- A mutation requires explicit confirmation and UAC.
- The helper validates live state after elevation and immediately before mutation.
- VHD initialization is bound to both the exact image path and native physical disk number.
- Pre-existing VHDX files are never overwritten.
- VHD failures are rolled back when safe; uncertain state is surfaced and retains its receipt.
- Resize requires one explicit confirmation, UAC, live verification, and immediate revalidation, but
  remains non-atomic and is not automatically reversible.
- Unit and safe UI tests never create, attach, shrink, partition, or format a real disk.
