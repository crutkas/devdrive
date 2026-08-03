# Dev Drive Storage prototype

This packaged WinUI 3 app is a deterministic, read-only UX workbench. It consumes
`IStorageSnapshotSource` from `DevDriveStorage` and ships only
`MockStorageSnapshotSource`; it contains no live storage or provider adapters.

Run from this directory:

```powershell
.\BuildAndRun.ps1
```

The reusable boundary is `DevDriveStorage`: immutable physical models, optional
provider context, strict scenario validation, explorer interaction state, and
pure treemap geometry. `DevDriveStoragePrototype` contains only WinUI rendering,
responsive layout, theme resources, and mock scenario controls. A later live
source can implement `IStorageSnapshotSource` without changing the explorer
ViewModel's public interaction model.

Run UI automation after launch:

```powershell
..\DevDriveStoragePrototype.UITests\ui-tests.ps1 -AppPid <launched-pid>
```
