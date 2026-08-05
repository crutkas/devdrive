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
- `BulkObservableCollection<T>` — `ReplaceAll` raises a single `Reset`.

## Streaming, and what a partial means

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
