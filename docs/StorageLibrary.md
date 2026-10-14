# The storage library

`DevDriveStorage` is the UI-agnostic half of the Space room and the storage
analyzer: immutable models, the snapshot-source contract, the explorer's
interaction state, and pure treemap geometry. The manager renders it; nothing in
here knows that WinUI exists.

## Projects

| Project | Role |
| --- | --- |
| `DevDriveStorage` | Models, snapshot source contract, live and mock sources, explorer state, treemap geometry, collection reconciliation |
| `DevDriveStorage.Tests` | MSTest unit suite |

`DevDriveStorage` references only .NET and `CommunityToolkit.Mvvm`. It must not
reference `Microsoft.UI.Xaml` or any UI type. A `Brush`, a `Visibility` or a
`DispatcherQueue` appearing in that project is a review failure.

> A mock-only WinUI host (`DevDriveStoragePrototype`) used to sit alongside this
> library to iterate on the UX. The Space room replaced it and it has been
> deleted. Its scenarios live on as the mock source below, exercised headlessly.

## What lives here

- `StorageSnapshot`, `StorageNode`, `StorageProviderContext`, `ScanCoverage` —
  immutable models. Physical identity (path, bytes, kind) is independent of
  optional provider metadata.
- `IStorageSnapshotSource` — the async contract, with scope, refresh ordinal,
  progress, cancellation, partial coverage, and explicit failure.
- `LiveStorageSnapshotSource` — real enumeration. Streams partial snapshots
  while it walks. This is what the app runs on.
- `MockStorageSnapshotSource` — strict, versioned JSON scenarios. This is what
  most tests run on.
- `RoutingStorageSnapshotSource` — picks between them on a `live:` prefix.
- `StorageExplorerViewModel` — breadcrumb, scope, selection, table mode,
  sorting, search, progress, errors, and refresh reconciliation.
- `CollectionReconciler` — merges a desired ordered list into a live
  `ObservableCollection` by id, so a streaming scan edits rows instead of
  replacing them.
- `TreemapLayout` — pure squarified layout returning rectangles. No rendering.
- `NestedTreemapLayout` — pure nested layout built on `TreemapLayout`: every
  folder with room opens into its own children under a title strip. No rendering.
- `BulkObservableCollection<T>` — `ReplaceAll` raises a single `Reset`.

## Streaming, and what a partial means

Space uses one native file-and-folder TreeView on the left and the current scope's
treemap on the right. The duplicate item table and Folders / Largest files selector
are intentionally gone (the library's largest-files projection remains available).
Selecting a folder drills into it; selecting a file shows its containing scope and
highlights that file. A full breadcrumb above the treemap is intentionally omitted;
the treemap's own scope strip (below) leads back out. Sizes and accessible
descriptions remain on tree rows. Filled folder icons and file icons use the active
treemap projection's stable color identities, not recomputed ranks. Off-scope and
zero-byte rows, and rows outside the treemap's 24-item limit, use the native secondary
foreground. The hierarchy retains its scrollbar gutter inside one native card shared
with the treemap. Each page has one standard route title: the shell supplies it for
most pages; Space hosts its own title/action row using the same native style.

A native, 48-DIP-high volume ComboBox matches the Scan button's height. Its semibold
name and free-space text share one line, separated from the minimal 2-DIP capacity
bar by 7 DIPs of vertical space, in both the selected presentation and dropdown.
The selector and Scan are right-aligned alongside the left-aligned Space title.
Below a 700-DIP window width they wrap together beneath the title rather than collide.
Unknown capacity is stated, not drawn as an empty disk. Selection survives navigation
without starting work; the adjacent play-icon **Scan** button starts the selected scan.
During a scan the picker is disabled, and **Scan** is hidden and replaced in the same
header slot by a **Cancel** button with a live progress ring. Progress floats over
the bottom of the card on a native elevated, shadowed surface, centered with a
maximum width of 560 DIPs; it never reserves a footer row. Its 12-DIP status text
and progress bar (indeterminate when unmeasured) share one horizontal row. Toolkit
`Implicit.ShowAnimations` / `HideAnimations` fade and translate it; collapsing it
leaves no reserved space. Windows' reduced-motion preference disables these animations.
Partial/denied coverage and exclusions remain in a main-content InfoBar after completion.
Errors and cancellation retain their main-content messages. Shared collection detachment
and scan lifetime are unchanged.

Right-click a tree item or treemap rectangle for **Open in File Explorer**.
Folders open directly; file entries open their containing folder. This uses the
clicked item's path, independently of selection, and reports unavailable folders.

The treemap is nested, in the style of SpaceSniffer. Each folder with room opens into
its own children under a title strip, up to three levels below the scope and 1,800
rectangles in total. Each folder draws its 24 largest children and folds the rest into
one block. Bytes a folder reports but has not listed yet, such as files mid-scan, show
as an **Unlisted** block. Top-level rectangles take their tree row's colour; nested
rectangles inherit it.

The map is drawn with `Microsoft.UI.Composition` visuals, one per rectangle, rather than
XAML elements:

- Implicit animations move and resize rectangles as a scan streams in or the window resizes.
- Selecting a folder zooms into it, and going up zooms back out.
- Selecting a file selects it in place.
- With Windows' reduced-motion preference on, each of these animations becomes an instant cut.

Labels, keyboard stand-ins and the hover card are thin XAML layers over the visuals.
Tab reaches each top-level rectangle and Enter drills in. Backspace, Alt+Up or the
mouse Back button goes up. Resting the pointer on a rectangle shows its size, share
of the scope and path.

A fixed-height scope strip always tops the map, SpaceSniffer-style. At the volume
root it shows a drive glyph, the root name and its size. Below the root it leads with
a native button, the parent folder's rectangle (up arrow plus parent name), then a
chevron, the current folder and its size. Clicking the parent zooms back out one
level. The strip keeps the same height at every scope, so the map never resizes
mid-zoom; it only fades out when there is no snapshot.

Tree bindings use `MaterializedChildren` and `HasUnrealizedChildren`; expanding a
branch materializes just its immediate children, including files. Streaming updates
reconcile only materialized branches, preserving identity and expansion instead of
replacing the hierarchy. File rows arrive with the final snapshot, not folder-only
partials. Detaching the page disconnects every shared collection source, including
the treemap, without writing a cleared selection back to the shared view model.
The tree always shows the whole volume hierarchy, so ancestors stay reachable there
too. Going up restores the parent scope and selection and does nothing at the root or
before a scan. The library still maintains breadcrumbs internally, but Space no longer
binds a native BreadcrumbBar to them.

A cold full-volume scan takes minutes, so `LiveStorageSnapshotSource` publishes
partial snapshots as it walks. Three rules make that safe to bind to:

- **Node ids are SHA-256 of the lowercased path, cached.** Every partial names
  the same folder the same way, which is the whole reason selection, expansion
  and scroll survive a tick.
- **Partials carry folders only.** Files are ~95% of a source tree and no file
  row is on screen mid-scan, so emitting them buys nothing and costs everything.
  A partial folder's size is a full rollup and therefore *exceeds* the sum of
  its emitted children.
- **A folder that has not been walked is not zero.** `StorageNode.IsMeasured` is
  false between the moment a folder is listed by its parent and the moment it is
  opened, and the display properties return an em dash rather than `0 B`.
  Denied folders stay unmeasured, because there the answer is unknowable rather
  than merely pending.

Throttling is `max(interval, 2 x last emit cost)`, which self-limits to about a
third of scan time at any tree size without a machine-specific threshold.

## How the live scan walks

`LiveStorageSnapshotSource` drives `Live/ParallelVolumeScanner`:

- **One worker per logical core** pulls folders off a shared queue. Each folder
  is opened once (`CreateFileW` with `FILE_LIST_DIRECTORY | SYNCHRONIZE` and
  backup semantics) and listed with `NtQueryDirectoryFile` into a 256 KB
  per-thread buffer, using `FileIdFullDirectoryInformation` and falling back to
  `FileFullDirectoryInformation` where the file system rejects it. No file is
  ever opened; size on disk is the entry's `AllocationSize`.
- **Junctions, symlinks and mount points are not followed** (reparse tags with
  the name-surrogate bit). Other reparse folders are, including OneDrive
  folders, whose tag reads back as zero from a listing.
- **NTFS hard links count once**, de-duplicated by file id. Cloud-only
  placeholders (`RECALL_ON_DATA_ACCESS`) allocate nothing and are tallied
  separately.
- **Elevated, it enables SeBackupPrivilege** so locked system folders can be
  listed. As a standard user those folders are counted as unreadable.
- **Files never get an object.** Each folder keeps running totals plus a min-heap
  of its biggest files (capped at the per-folder child limit); a name string is
  only allocated when a file enters that heap. Folder ids are assigned at
  discovery, so a child always follows its parent and the rollup is one
  backward pass.
- **Partials freeze the walk only for the rollup.** Workers hold a shared lock
  while they mutate a folder; a partial takes it exclusively just long enough to
  roll totals up (milliseconds), then emits while the walk continues, reading
  only what that rollup captured. A folder unfinished at the rollup is emitted as
  unmeasured with no children.
- **The app runs workstation GC.** A full system-drive scan allocates over a
  million nodes. Server GC saved about two seconds of gen0/gen1 pauses on a
  20-core machine but held roughly 50% more memory, so it stays off (see
  `DevDriveManager.csproj`, and SH-016 in `KnownIssues.md` for a GC/XAML
  deadlock first seen under server GC).
- **A file node does not store its full path.** It keeps a reference to its
  folder's path string (one instance per folder) and `PhysicalPath` joins the
  name on read; ids are still hashed from the full path at emit, so they stay
  stable across partials. Apparent size and provider context, set on a few
  percent of nodes, sit behind one optional reference, and the date is stored as
  ticks. On a 1.15M-node C: scan this took the retained snapshot from 645 MB to
  301 MB. Because paths are not stored, the snapshot checks for duplicate names
  within a folder rather than duplicate paths across the volume.

The finished snapshot's `ScanCoverage.Statistics` carries the headline numbers
(files, hard-link duplicates, folders, unreadable folders, size on disk, the
drive's used space, cloud-only bytes, query and total time). The redesigned
Space room does not display them yet; `StorageExplorerViewModel.ScanStatisticsSummary`
still exposes them.

## Swapping a source

Only one seam changes: implement `IStorageSnapshotSource` and inject it where
the source is constructed (`App.xaml.cs`). `StorageExplorerViewModel`'s public
interaction model does not change.

A source is responsible for honouring the cancellation token, reporting
`StorageScanProgress`, and reporting reduced `ScanCoverage` rather than silently
omitting denied paths.

## Accepted limitations

- Cold full-volume scans are I/O bound. Streaming and cancellation are the
  mitigation, not a fix.
- ~1 KB/node resident. A 10M-file volume would want a bounded-node design.
- Long paths over 260 characters are reported as denied, not traversed.
- Hardlinks are not deduplicated, so a hardlink-heavy tree over-reports.
- Folder self-allocation is 0 — no directory-entry slack or MFT overhead.
- Provider correlation is path-heuristic only and always marked `Stale`.

## Testing

```powershell
dotnet test DevDriveStorage.Tests\DevDriveStorage.Tests.csproj -p:Platform=x64
```

The live-scanner tests build real disposable trees under `%TEMP%` via
`LiveScanFixture` — sparse files, junctions, and denied directories included.
Nothing touches the developer's own data.

The room that renders all of this is covered by `DevDriveManager.UITests`.
For a safe UI scan test, Debug builds accept `--space-ui-fixture` via `winapp run`.
This explicitly replaces only Space's volumes/source with synthetic scenarios (complete,
partial, failure/retry, cancellable, large hierarchy, unknown capacity); it never scans
real disks. Restart without the argument to restore the live source. The fixture switch
is not compiled into Release builds.

```powershell
winapp run .\DevDriveManager\DevDriveManager.csproj --debug-output --args --space-ui-fixture
# Use the PID printed by winapp, in a second terminal:
.\DevDriveManager.UITests\space-fixture-smoke.ps1 -AppPid <PID>
.\DevDriveManager.UITests\navigation-smoke.ps1 -AppPid <PID> -IncludeThemeChecks
```

The scan smoke test refuses to proceed unless it sees the explicit fixture banner and
six synthetic picker entries. The navigation smoke test never scans or mutates storage;
theme and warning-preference checks restore their original values.
