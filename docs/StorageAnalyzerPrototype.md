# Storage analyzer prototype

A mock-data-only WinUI 3 prototype of the Dev Drive storage space analyzer. It
exists to iterate on the UX quickly, ahead of merging the feature into Dev Drive
Manager.

**The prototype never reads or mutates real machine storage.** Every byte, path,
item count, and provider label on screen comes from a versioned JSON scenario.

## Projects

| Project | Role | Merges back? |
| --- | --- | --- |
| `DevDriveStorage` | UI-agnostic models, snapshot source contract, explorer state, treemap geometry | **Yes** |
| `DevDriveStorage.Tests` | MSTest unit suite for the library | **Yes** |
| `DevDriveStoragePrototype` | Disposable WinUI host: page layout, controls, prototype affordances | No |
| `DevDriveStoragePrototype.UITests` | Batch `winapp ui` suite and screenshot checklist | Partly |

`DevDriveStorage` references only .NET and `CommunityToolkit.Mvvm`. It must not
reference `Microsoft.UI.Xaml`, the prototype app, or machine APIs. A UI type
appearing in that project is a review failure.

## Reusable vs prototype-only

Reusable (`DevDriveStorage`):

- `StorageSnapshot`, `StorageNode`, `StorageProviderContext`, `ScanCoverage` —
  immutable models. Physical identity (path, bytes, kind) is independent of
  optional provider metadata.
- `IStorageSnapshotSource` — the production-shaped async contract, with scope,
  refresh ordinal, progress, cancellation, partial coverage, and explicit
  failure.
- `StorageExplorerViewModel` — breadcrumb, scope, selection, table mode,
  sorting, search, progress, errors, and refresh reconciliation.
- `TreemapLayout` — pure squarified layout returning rectangles. No rendering.
- `BulkObservableCollection<T>` — `ReplaceAll` raises a single `Reset` so a
  2,500-row scenario switch re-renders the treemap once instead of 2,501 times.

Prototype-only (`DevDriveStoragePrototype`):

- `MainPage` layout, `TreemapPane` rendering and hit-testing, `InspectorPane`.
- The **Prototype controls** expander (scenario, layout, theme pickers). It is
  collapsed by default and lives outside the reusable page content.
- Fixed 1600x900 / 1280x800 window sizing.

## Mock scenarios

Scenarios are strict, versioned JSON validated by `StorageScenarioSerializer`.
Adding or changing a state should be a data change, not a page rewrite.

Covered: baseline 712 GB, scripted scanning and cancellation, partial coverage
with denied paths, scan failure and retry, empty drive, no provider metadata,
stale provider metadata, provider unavailable, awkward names (deep paths,
Unicode, duplicate leaf names), a large deterministic hierarchy for
virtualization stress, and a refresh that removes and resizes the selection.

## Swapping in a live source

Only one seam changes. Implement `IStorageSnapshotSource` against the real
scanner and inject it where `MockStorageSnapshotSource` is constructed today.
`StorageExplorerViewModel`'s public interaction model does not change, so the
UI tests keep their meaning.

The live source is responsible for honouring the cancellation token, reporting
`StorageScanProgress`, and reporting reduced `ScanCoverage` rather than
silently omitting denied paths.

## Testing

```powershell
dotnet test DevDriveStorage.Tests\DevDriveStorage.Tests.csproj

# Launch, then point the batch UI suite at the running process.
winapp run DevDriveStoragePrototype\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64 --detach
DevDriveStoragePrototype.UITests\ui-tests.ps1 -AppPid <pid>
```

The UI suite runs every scenario in one pass and writes `test-results.json`
plus a numbered screenshot checklist. Screenshots require an interactive
desktop session; window capture returns black frames over a locked session even
though the UI Automation assertions still pass.

## Diagnostics

`Frame.Navigate` swallows page construction failures, so a XAML error in
`MainPage` surfaces as a silently blank window rather than a crash.
`MainWindow` therefore handles `NavigationFailed`, and `App.OnLaunched` wraps
window construction, both writing the full exception to
`%TEMP%\devdrive-prototype-startup-error.txt`. Check that file first when the
window opens empty.

## Out of scope

Real filesystem, NTFS, USN, or MFT scanning; elevation and provider execution;
cleanup actions, review flows, queues, history, or receipts; persistence of
real scans; changes to the existing manager NavigationView or shared
ViewModels.
