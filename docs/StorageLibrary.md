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
header slot by a **Cancel** button with a live progress ring. Every room's Scan starts
both the Space walk and the Reclaim scan (`App.ScanEverything`), so every room's Cancel
stops both through one `ScanCoordinator` (`App.Scans`). Overview and Reclaim show that
combined state in their scan bar — a ring, which half is still running, and Cancel —
until both halves finish. Progress floats over
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

## Reclaim: removed is not freed

The Reclaim room sits beside Space and reads the same volumes, so its numbers have to
agree with what Space will measure afterwards. Three rules keep them honest
(`DevDriveReclaim/ReclaimPlan.cs`, `RecycleBinPreflight.cs`, `ReclaimExecution.cs`, `ReclaimVolume.cs`):

- **Recycling frees nothing yet.** `$Recycle.Bin` lives on the same volume, so an item
  moved there still occupies it. Outcomes split `BytesFreed` (deleted outright, or a bin
  emptied) from `BytesRecycled`. The confirmation, progress line, run summary, and
  per-volume after-picture only move free space by the freed part; recycled bytes are
  reported as "to the Recycle Bin — frees when emptied".
- **A promise of the Recycle Bin is kept.** Before the confirmation, each item that asks
  for the bin is checked against that volume's bin settings (its size limit, "don't move
  files to the Recycle Bin", the `NoRecycleFiles` policy, volumes with no bin). Anything
  the bin would refuse is listed as permanent, with the reason, before the user agrees.
  At run time the executor re-reads the settings and **keeps** — rather than deletes — any
  item it promised to recycle that the bin now refuses, and asks the shell
  (`FOF_WANTNUKEWARNING`) to warn before any permanent delete nothing predicted.
- **The volume is the one the bytes are on.** A source root can be a junction or symbolic link to
  another volume, and the scan reaches everything under it by the link's path. A row's `VolumeRoot`
  follows the path through every link to where it leads (`ReclaimVolume`, the resolution
  `ReclaimPathGuard` acts on), so the bin the plan and the run check an item against, and the volume
  whose bar moves, are the ones the item really leaves. A path that can't be opened is placed by its
  nearest ancestor that can; one nothing resolves keeps its spelled root. Network paths are never
  opened.

## Reclaim: build output is what git doesn't track

Build outputs are found by folder name (`bin`, `obj`, `build`, `out`, `dist`, `target`,
`node_modules`, …), and a name is a guess: plenty of repositories commit a `build` folder of
pipeline files or a `bin` of tools. So each repository's candidate folders are put to git once
(`IWorktreeInspector.TrackedFolders`, one bounded `git ls-files` per repository). A folder git tracks
any file in isn't offered as generated output; when git can't answer (missing, timed out, a
repository it refuses), the folder is offered as Check, not Safe. An ignored folder with nothing
committed in it is still Safe, and a `.git` that isn't a repository to git keeps the name's answer.

## Reclaim: asked again before it goes

A scan grades each row on what it found then, and a run can start hours later. A row whose grade
rests on something that can change is asked about again just before it goes
(`DevDriveReclaim/ReclaimRecheck.cs`), and **kept** when the answer is riskier now: the outcome is
`Failed`, "kept — changed since the scan: …", nothing is freed, and `ReclaimItemOutcome.Change` says
what changed as data. The dry run (`SafeFakeReclaimExecutor`) asks the same questions; they only read.

- **Worktrees and dormant clones** carry what git said at the scan (`ReclaimCandidate.GitEvidence`).
  The run asks the same `IWorktreeInspector` again and keeps the row when it has uncommitted changes
  (or more of them), commits no remote has, no upstream, another branch or commit or a detached HEAD,
  a branch no longer merged, a new local-only ignored file (a `.env`), more stashes (a dormant
  clone), or when git can't answer. A row graded without git (an orphaned worktree folder) has
  nothing to ask about.
- **Duplicates** name the copy kept in their place (`ReclaimCandidate.KeptCopy`). Nothing under
  `$Recycle.Bin` or `System Volume Information` is a copy, kept or offered; the engine doesn't offer a
  row whose kept copy is inside another row of the scan (a build output, a dormant project, a
  model); and the run keeps one whose kept copy is gone, isn't the same size any more, or is being
  removed in the same run ("kept — the copy kept in its place, …, is being removed in this run too").

## Reclaim: AI models

The **AI models** category (`DevDriveReclaim/Providers/Models`) lists models that local model
managers downloaded, one row per model as its manager names it, and removes them through that
manager. Each manager's folder is found the way the manager finds it (`LocalModelLocations`):

| Manager | Where | A row is | Removed with |
|---|---|---|---|
| Ollama | `OLLAMA_MODELS`, else `%USERPROFILE%\.ollama\models` | the names whose manifests (`manifests-v2` and `manifests`) share large blobs | `ollama rm <every name>` |
| Hugging Face | `HF_HUB_CACHE`, `HUGGINGFACE_HUB_CACHE`, `HF_HOME\hub`, else `%USERPROFILE%\.cache\huggingface\hub`; llama.cpp's `LLAMA_CACHE` too | a `models--org--name` folder, every revision | its folder and each shared stored file only it used (as `hf cache rm` does) |
| LM Studio | `~\.lmstudio-home-pointer`'s folder, `~\.cache\lm-studio` or `~\.lmstudio`, then `models` (or `settings.json`'s `downloadsFolder`) | a `publisher\model` folder with `.gguf` files | its folder (as My Models › Delete does) |
| Foundry Local | `foundry cache location`, else `%USERPROFILE%\.foundry\cache\models` | a `publisher\model` folder with an `inference_model.json` | `foundry cache remove <id> --force` |
| Model files | GPT4All's folder, and folders the host adds | a `.gguf` file | the file |

- **Sizes count a file once.** An Ollama blob two rows use, a Hugging Face stored file
  (`blobs\<xx>\<hash>`, listed in its `.refs`) another repo links to, or a file with another hard
  link is freed by neither row alone, so it's in neither's size (the row says how much it shares).
  Symbolic links cost nothing.
- **A place that can't be read is a gap, not zero.** Providers report it with
  `ReclaimScanContext.ReportGap`; it reaches `ReclaimCategoryResult.Unread`, and
  `ReclaimResult.PartlyReadCategories` says the total is a floor. A running Ollama that lists models
  this folder doesn't have is a gap too.
- **Nothing in use is removed.** `ReclaimCandidate.Owner` sends a model to `IReclaimOwnerRemoval`
  (`LocalModelRemoval`) instead of a path delete. It keeps a model Ollama or Foundry Local has loaded,
  one whose manager isn't installed, and an Ollama model whose names changed since the scan. `ollama rm`
  runs only against a running Ollama that serves each name from the manifest scanned (`/api/tags`'
  digest is the manifest file's SHA-256) and loads its weights from this folder (`/api/show`'s `FROM`
  path is the same file as this folder's blob; a pulled manifest is the same bytes in every folder),
  with that Ollama's `OLLAMA_HOST`, which must be this PC, so it never removes from another models
  folder and never has to start the app. A model's files, and the Hugging Face stored
  files only it uses (under huggingface_hub's locks), are deleted together or not at all
  (`ModelFiles.DeleteTogether`): each is opened exclusively and given the classic delete disposition,
  which Windows refuses while a program has the file open or mapped, as llama.cpp maps a loaded model.
  `WhyKept` is read-only, for a dry run; it sees open files, not a mapping.
- **Every row is Check and permanent.** A model downloads again, but it can be big, and one someone
  made or imported may exist nowhere else. Nothing is called idle and nothing is preselected.
- **Not read yet:** AI Toolkit for VS Code (deleting its files would leave its `my-models.yml` stale),
  AI Dev Gallery, Jan, and PyTorch Hub and Whisper caches. Never: Windows' own AI models, which
  Windows manages.

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
