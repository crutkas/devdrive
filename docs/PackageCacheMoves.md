# Moving package caches — how it works and every fallback

Moving a package cache (npm, NuGet, pip, Cargo, …) onto the Dev Drive is the app's headline reversible
mutation. This page documents the mechanism **and, deliberately, every fallback** — what happens when a
move is interrupted, fails a check, is cancelled, is already done, or needs to be undone.

- Engine: `DevDriveCore/Services/PackageCacheMover.cs` (implements `IPackageCacheMover`)
- UI orchestrator: `DevDriveCore/Services/PackageCacheMoveCoordinator.cs`
- Options: `DevDriveCore/Models/CacheMoveOptions.cs`
- Catalogue: `DevDriveCore/Services/PackageCacheCatalog.cs` (see [Configuration.md §6](Configuration.md))
- Composition (real vs. test): `DevDriveManager/Services/MutationComposition.cs`

> **It is wired.** The app uses the **real** mover via `PackageCacheMoveCoordinator` (composed by
> `MutationComposition.CreatePackageCacheMoveCoordinator`). Under the test seam
> `DDM_UITEST_SAFE_MUTATIONS=1` it swaps in an in‑memory `SafeFakePackageCacheMover` instead, so UI
> tests never touch a real cache.

---

## 1. What a move actually does

A "move" with the **default options is a verified copy that keeps your original** — *not* a destructive
move. The sequence (`PackageCacheMover.MoveCore`):

1. **Capture prior state.** Read the current value of the per‑user environment variable (e.g.
   `npm_config_cache`) so the move can be undone and is idempotent.
2. **Idempotency check.** If the variable already points at the target on the Dev Drive, do nothing and
   report `AlreadyOnDevDrive` (success, no‑op).
3. **Copy.** Create the target folder and copy every file from the source to the Dev Drive.
4. **Verify.** With `VerifyHashes` on (default), compare each copied file's **SHA‑256** against the
   source; a mismatch aborts the move.
5. **Commit — only after every file is copied and verified:** set the per‑user environment variable to
   the new location.
6. **Source handling.** With the default options the **source is kept in place** (`DeleteSourceAfterVerify
   = false`), so revert is trivial. (A true "move" that deletes the source is possible but not the
   default.)
7. **Record reversibility.** Save a `ReversibilityEntry` (prior value, source, target, whether the
   source was deleted) to the persistent store so **Move back** works later — even after a restart.

### Default options (`CacheMoveOptions.Default`)

| Option | Default | Meaning |
| --- | --- | --- |
| `Overwrite` | `true` | Overwrite existing destination files. |
| `VerifyHashes` | `true` | SHA‑256‑verify every copied file; a mismatch fails the move and triggers rollback. |
| `DeleteSourceAfterVerify` | **`false`** | **Keep the source.** The move is a verified copy; the original is left untouched so undo only has to restore the environment variable and delete the copy. |

The ordering is the safety guarantee: **the environment variable is changed only after the whole copy
is verified.** Until that moment your tools still use the original cache.

---

## 2. Fallbacks — what happens when something goes wrong

The coordinator (`PackageCacheMoveCoordinator`) **never throws for an expected failure**; it projects
every outcome into a `CacheMoveOutcome` with a clear status, so a *Move all* sweep keeps going.

| Situation | What the engine does | What you see |
| --- | --- | --- |
| **Mid‑copy failure** (disk full, file locked, I/O error) | Roll back: delete every file already copied, and remove the target folder if the move created it. The environment variable was **not** changed yet, and **no** reversibility entry was written. | **Failed** (with the error message). Your machine is exactly as before — source intact, variable unchanged. |
| **Hash mismatch** (`VerifyHashes`) | Treated as a failure → same rollback as above. | **Failed** — "integrity check failed". Nothing committed. |
| **Cancellation** (you cancel, or a sweep is cancelled) | The partial copy is rolled back the same way; the variable stays unchanged. | **Cancelled.** |
| **Already on the Dev Drive** (variable already points at the target) | No‑op. | **Success / already on Dev Drive** — no files copied, nothing changed. |
| **"Move all" with one cache failing** | The failing cache becomes a Failed row; the sweep continues to the others. | A per‑row result; one failure doesn't abort the rest. |

The key invariant: **a failed or cancelled move leaves your system in its original state.** Because the
variable is the *last* thing written and is written only after a full verified copy, there's no
half‑moved state where your tools point at an incomplete cache.

> Rollback of the copied files is **best‑effort** (a file the OS still has locked may linger in the
> target folder), but the important guarantee holds regardless: the environment variable is never
> changed on a failed move, so your tools keep using the original, intact cache.

---

## 3. Move back (undo) — and why it survives a restart

Every successful move records a `ReversibilityEntry`. **Move back**
(`PackageCacheMoveCoordinator.MoveBackAsync`):

1. **Restore the environment variable** to its prior value — or *clears* it if it wasn't set before the
   move (so you end up exactly where you started, not with a stray empty variable).
2. **Delete the Dev Drive copy.** Because the default move kept your source in place, the files are
   **not** moved back — the original is already there. (If a move had been run with
   `DeleteSourceAfterVerify`, revert can copy the files back to the source instead.)
3. **Remove the reversibility entry.**

Returns **Moved back** on success, or **Nothing to revert** if no entry was found.

**Why it survives an app restart:** the reversibility entry is written to a **persistent** store —
`%LocalAppData%\DevDriveManager\reversibility.json` (`JsonFileReversibilityStore`). The coordinator's
`CanMoveBack(variable)` reads that store, so a cache moved in one session still shows a **Move back**
affordance the next time you open the app.

---

## 4. Per‑user, no admin

Cache moves set **per‑user** environment variables (`UserEnvironmentWriter`) — never machine‑wide — so
they **don't require administrator** and only affect your account. That's also why they're safe to undo.

---

## 5. The npm special case

npm's cache location isn't simply its default folder. `NpmCacheLocator` resolves the real, current
location in order: **`npm_config_cache`** → **`npm config get cache`** → the `%LocalAppData%\npm-cache`
default. The move targets whatever is actually in use, so it works whether or not you've already
customised it.

---

## 6. How this is tested without touching your machine

Under `DDM_UITEST_SAFE_MUTATIONS=1`, `MutationComposition` composes a `SafeFakePackageCacheMover` that
**simulates** the move (reports progress, records a reversibility entry in an in‑memory store) without
copying a single real file or setting a real environment variable, and the UI shows a *"safe mutation
mode"* indicator. This lets the automated UI suite drive the full **Move → result → Move back** flow
end‑to‑end while guaranteeing **zero** real changes. In normal operation the real
`PackageCacheMover` over the real filesystem + per‑user environment is used. (Unit tests exercise the
real engine against an in‑memory filesystem + fake environment writer, or a throwaway temp directory.)

---

## 7. Examples by tool

Every move follows the same shape: **copy the cache to `Dev:\packages\<tool>` (source kept) → set the
per‑user environment variable → record the undo**. The drive letter and folder come from the catalogue
(`PackageCacheCatalog`); the target is always `Dev:\packages\<tool>`. After a move, **restart your
shells/terminals** so the new per‑user value is picked up (open shells keep the old value until then).

| Tool | Variable set (per‑user) | Default location | Dev Drive target | Verify (new shell) |
| --- | --- | --- | --- | --- |
| **npm** | `npm_config_cache` | `%LocalAppData%\npm-cache` * | `G:\packages\npm` | `npm config get cache` → `G:\…`; `npm cache verify` |
| **Gradle** | `GRADLE_USER_HOME` | `%UserProfile%\.gradle` | `G:\packages\gradle` | `gradle --version`, then check `caches\` appears under the target |
| **Go modules** | `GOMODCACHE` | `%UserProfile%\go\pkg\mod` | `G:\packages\gomodules` | `go env GOMODCACHE` → `G:\…`; `go mod download` populates it |
| **Yarn** | `YARN_CACHE_FOLDER` | `%LocalAppData%\Yarn\Cache` | `G:\packages\yarn` | `yarn cache dir` → `G:\…` |

> \* The move targets your **actual** current location, not just the default — npm's is resolved via
> `npm_config_cache` → `npm config get cache` → the default (see §5), so it works even if you've pinned a
> custom path (env var or `.npmrc`).

### npm — worked example
```
copy   C:\…\npm-cache  →  G:\packages\npm        (source kept)
setx   npm_config_cache = G:\packages\npm        (per-user; wins over a Machine-scope value in new shells)
verify (new shell):  npm config get cache  →  G:\packages\npm
                     npm cache verify      →  "Cache verified" (G:\packages\npm\_cacache)
undo:  Move back  →  removes the per-user variable (npm falls back to its prior location) + deletes the G: copy
```

### Gradle
`GRADLE_USER_HOME` relocates the **whole Gradle home**, not just one folder — the dependency cache
(`caches\modules-2\…`) **and** the downloaded wrapper distributions (`wrapper\dists\…`), which are the
bulk of the small‑file churn a Dev Drive accelerates. It also holds `init.gradle`, `gradle.properties`,
and daemon logs; moving the home moves those too (that is the standard, supported relocation). After the
move, the next build writes `G:\packages\gradle\caches\…`.

### Go modules
`GOMODCACHE` is the **download cache** for modules (`pkg\mod`) — the many small, content‑addressed files a
Dev Drive helps with. Two things to know:
- Go marks module‑cache files **read‑only**; the mover clears the read‑only bit when copying/cleaning, so
  the move and the undo both work (a plain `del` would fail — that's why `go clean -modcache` exists).
- This moves **only** the module cache. Go's separate **build cache** is `GOCACHE` (default
  `%LocalAppData%\go-build`) — not in this catalogue; relocate it the same way with `go env -w GOCACHE=…`
  if you want it on the Dev Drive too.

### Yarn
`YARN_CACHE_FOLDER` is the global cache for **Yarn Classic (v1)** (default `%LocalAppData%\Yarn\Cache`).
For **Yarn Berry (v2+)** the cache defaults to a **per‑project** `.yarn\cache` instead; `YARN_CACHE_FOLDER`
(or `yarn config set cacheFolder …`) only governs a *global* cache, which Berry uses when
`enableGlobalCache` is on. So this entry cleanly relocates Classic Yarn; on Berry it applies to the global
cache, and per‑project caches move with the project (put your repos on the Dev Drive — see *Suggestions*).
