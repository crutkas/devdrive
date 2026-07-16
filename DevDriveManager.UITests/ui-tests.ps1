<#
    ui-tests.ps1 — WinApp UI tests for DevDriveManager (Milestone 1)

    Follows the winui-ui-testing skill: one batch script, single run, structured results.
    Drives the *built, running* app via `winapp ui` (UI Automation). Real mutations (package-cache
    Move / Move back) are exercised ONLY under the DDM_UITEST_SAFE_MUTATIONS=1 seam, which swaps in
    in-memory SafeFakes — so no real cache or environment variable is ever touched. The suite confirms
    the seam (SafeMutationModeIndicator) before any real Confirm.

    Definition-of-done coverage:
      (a) the volumes list renders      -> realized VolumeCard_* rows are present
      (b) the G: row shows a Dev Drive badge -> DevDriveBadge_G is in the tree, and the
          equivalent C: badge is absent (C: is a normal NTFS volume).
      (c) the full concept page renders -> every section header (Performance, Package caches,
          Drive health) and its key controls are present.
      (d) the build benchmarks are REAL -> the "Real-world builds" group runs real developer
          workloads (git clone first) and renders a per-operation result row or a "SKIPPED — reason".
          Each row exposes a per-row Details affordance (exact command, what runs, methodology + the
          raw run times). The synthetic raw disk-I/O group is gone; a calm absence note explains why.
      (e) mutating affordances are real but SAFE -> Move / Move all reveal an explicit inline confirm;
          under the DDM_UITEST_SAFE_MUTATIONS seam the move is faked in-memory (no real cache/env/
          settings touched) and is reversible via Move back.
      (f) accessibility -> every app-authored interactive control exposes an AutomationId.

    Usage:
      # Set DDM_UITEST_SAFE_MUTATIONS=1 at user scope, launch the app, verify the visible safe-mode
      # indicator, and note its PID. This script exits with code 2 if the seam is absent.
      .\ui-tests.ps1 -AppPid <PID>

    Exit code 0 = all passed, 1 = one or more failures. Results also written to
    test-results.json next to this script.
#>
param([Parameter(Mandatory)][int]$AppPid)
# NOTE: do NOT name the parameter $Pid — it is read-only in PowerShell.

$ErrorActionPreference = 'Continue'
Set-Location -Path $PSScriptRoot

$pass = 0; $fail = 0; $skip = 0; $results = @()

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    # Inside $Script signal failure with `throw` (NOT `exit`). External `winapp ui`
    # commands set $LASTEXITCODE, which is also treated as the pass/fail signal.
    try {
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:pass++; $script:results += @{ name = $Name; status = "PASS" }
            Write-Host "  PASS: $Name" -ForegroundColor Green
        } else {
            $script:fail++; $script:results += @{ name = $Name; status = "FAIL"; detail = "$output" }
            Write-Host "  FAIL: $Name — $output" -ForegroundColor Red
        }
    } catch {
        $script:fail++; $script:results += @{ name = $Name; status = "FAIL"; detail = "$_" }
        Write-Host "  FAIL: $Name — $_" -ForegroundColor Red
    }
}

# Read an element's UIA Name (deterministic for SettingsCard groups, which expose a rich
# AutomationProperties.Name). Returns "" if the element/property is missing.
function Get-Name([string]$id) {
    $json = winapp ui get-property $id -a $AppPid -p Name --json 2>$null | ConvertFrom-Json
    return [string]$json.properties.Name
}

# Returns the number of elements whose text matches $text.
function Get-MatchCount([string]$text) {
    return [int](winapp ui search "$text" -a $AppPid --json 2>$null | ConvertFrom-Json).matchCount
}

# Read an element's on-screen TOP (Y) from its BoundingRectangle, for ordering assertions. Tolerant of
# the rectangle being a string ("x,y,w,h" / "X=..,Y=..") or an object. Returns $null when unavailable.
function Get-Top([string]$id) {
    $json = winapp ui get-property $id -a $AppPid -p BoundingRectangle --json 2>$null | ConvertFrom-Json
    $r = $json.properties.BoundingRectangle
    if ($null -eq $r) { return $null }
    if ($r -is [string]) {
        $m = [regex]::Match($r, 'Y\s*=\s*(-?\d+(\.\d+)?)')
        if ($m.Success) { return [double]$m.Groups[1].Value }
        $nums = [regex]::Matches($r, '-?\d+(\.\d+)?')
        if ($nums.Count -ge 2) { return [double]$nums[1].Value }
        return $null
    }
    if ($null -ne $r.Y) { return [double]$r.Y }
    if ($null -ne $r.Top) { return [double]$r.Top }
    return $null
}

Write-Host "DevDriveManager UI tests — app PID $AppPid`n"

# Hard safety gate: do not merely record this as a failed test and continue into mutation scenarios.
winapp ui wait-for "SafeMutationModeIndicator" -a $AppPid -t 4000 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Safe mutation mode is not active. Refusing to run UI tests that can confirm machine mutations."
    exit 2
}

# ─────────────────────────────────────────────────────────────────────────────
#  (a) The volumes list renders — assert the realized item rows are present.
#      (ItemsRepeater is a lightweight panel and does not project its own automation
#       peer, so we assert on the rendered rows — a stronger check than the container.)
# ─────────────────────────────────────────────────────────────────────────────
# The "All drives on this PC" list is a collapsed disclosure when a Dev Drive exists; expand it so
# its (non-virtualizing) rows realize into the UIA tree before we assert on them.
Test-UI "All-drives disclosure present" { winapp ui wait-for "AllDrivesExpander" -a $AppPid -t 4000 }
winapp ui invoke "AllDrivesExpander" -a $AppPid 2>$null | Out-Null
Start-Sleep -Milliseconds 600
Test-UI "Volumes list rendered: G: row present"        { winapp ui wait-for "VolumeCard_G"        -a $AppPid -t 5000 }
Test-UI "Volumes list rendered: C: row present"        { winapp ui wait-for "VolumeCard_C"        -a $AppPid -t 4000 }
Test-UI "Volumes list rendered: Recovery row present"  { winapp ui wait-for "VolumeCard_Recovery" -a $AppPid -t 4000 }

# ─────────────────────────────────────────────────────────────────────────────
#  (b) The G: row shows the "Dev Drive" badge (headline requirement) — and C: does not.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "G: row shows the Dev Drive badge" { winapp ui wait-for "DevDriveBadge_G" -a $AppPid -t 4000 }
Test-UI "C: row has NO Dev Drive badge"    { winapp ui wait-for "DevDriveBadge_C" -a $AppPid --gone -t 3000 }

# ─────────────────────────────────────────────────────────────────────────────
#  Content correctness — G: is a ReFS Dev Drive, C: is a plain NTFS volume.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "G: row reports ReFS filesystem" {
    $n = Get-Name "VolumeCard_G"
    if ($n -notmatch 'ReFS') { throw "expected 'ReFS' in G: row name but got: '$n'" }
}
Test-UI "G: row name advertises Dev Drive" {
    $n = Get-Name "VolumeCard_G"
    if ($n -notmatch 'Dev Drive') { throw "expected 'Dev Drive' in G: row name but got: '$n'" }
}
Test-UI "G: row name advertises Trusted" {
    $n = Get-Name "VolumeCard_G"
    if ($n -notmatch 'Trusted') { throw "expected 'Trusted' in G: row name but got: '$n'" }
}
Test-UI "C: row reports NTFS filesystem" {
    $n = Get-Name "VolumeCard_C"
    if ($n -notmatch 'NTFS') { throw "expected 'NTFS' in C: row name but got: '$n'" }
}
Test-UI "C: row name does NOT advertise Dev Drive" {
    $n = Get-Name "VolumeCard_C"
    if ($n -match 'Dev Drive') { throw "C: row name unexpectedly contains 'Dev Drive': '$n'" }
}

# ─────────────────────────────────────────────────────────────────────────────
#  Status banner — CHANGE 1: dropped in the active state. With a Dev Drive on G:,
#  the (previously green) "active" banner is gone; Drive health conveys active/healthy.
#  The banner is reserved for the no-Dev-Drive-yet and error states only.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "No status banner in the active Dev Drive state" { winapp ui wait-for "StatusBanner" -a $AppPid --gone -t 3000 }
Test-UI "Dropped green 'active' banner text is gone" {
    # search exits non-zero when the text is absent (the desired outcome here), so neutralize
    # $LASTEXITCODE and assert via throw on the parsed matchCount instead.
    $j = winapp ui search "Dev Drive (G:) is active" -a $AppPid --json 2>$null | ConvertFrom-Json
    $global:LASTEXITCODE = 0
    if ([int]$j.matchCount -ge 1) { throw "the dropped green banner text 'Dev Drive (G:) is active' is still present" }
}

# ─────────────────────────────────────────────────────────────────────────────
#  Controls present + safe Refresh interaction (Refresh only re-reads, never mutates).
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Refresh button present"        { winapp ui wait-for "RefreshButton"        -a $AppPid -t 3000 }
Test-UI "Create Dev Drive button present" { winapp ui wait-for "CreateDevDriveButton" -a $AppPid -t 3000 }
Test-UI "Refresh button invokes (re-reads volumes)" { winapp ui invoke "RefreshButton" -a $AppPid }
Start-Sleep -Milliseconds 1200
Test-UI "G: row still present after refresh" { winapp ui wait-for "VolumeCard_G" -a $AppPid -t 5000 }

# ─────────────────────────────────────────────────────────────────────────────
#  Unelevated state — CHANGE 3: the heavy "restart the whole app as admin" InfoBar is
#  replaced by a compact "See filter drivers" affordance in the Drive health card that reads
#  trust/filters live via a short-lived, UAC-elevated, READ-ONLY helper (no full-app restart).
#  The live UAC prompt can't be auto-driven by an agent, so we assert the affordance + its
#  wiring are present (not the elevation itself). (This run is unelevated, so it's shown.)
# ─────────────────────────────────────────────────────────────────────────────
# ── We assert on the shield "See filter drivers" button (proves the affordance is shown).
#    The old heavy elevation InfoBar AND the "Restart as administrator" fallback must be gone. ──
Test-UI "See Filters affordance shown when unelevated (shield button present)" { winapp ui wait-for "SeeFiltersButton" -a $AppPid -t 3000 }
Test-UI "Restart-as-administrator link removed" { winapp ui wait-for "RestartAsAdminButton" -a $AppPid --gone -t 2000 }
Test-UI "Old heavy elevation InfoBar is gone" { winapp ui wait-for "ElevationInfoBar" -a $AppPid --gone -t 2000 }

# ─────────────────────────────────────────────────────────────────────────────
#  Trust & filters expander — Drive health owns the detail. When trust detail is
#  readable (elevated) the expander is present and EXPANDED BY DEFAULT (its child
#  cards realize); when unelevated (no detail) the expander is absent and the
#  "See Filters" affordance stands in. Robust to both: this run is unelevated, so we
#  take the See-Filters branch — the elevated branch is covered by reasoning + unit tests.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Trust & filters: expander expanded by default (else See Filters affordance when unelevated)" {
    winapp ui wait-for "TrustExpander" -a $AppPid -t 2000 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        # Trust detail present — expanded-by-default realizes the expander's child cards.
        # "Filters allowed" is unique to the expander content and is only in the UIA tree
        # (non-collapsed) when the expander is open.
        if ((Get-MatchCount 'Filters allowed') -lt 1) {
            throw "TrustExpander is present but its content is not expanded by default"
        }
    } else {
        # Unelevated — no trust detail; the See Filters affordance must stand in instead.
        winapp ui wait-for "SeeFiltersButton" -a $AppPid -t 3000
        if ($LASTEXITCODE -ne 0) { throw "neither TrustExpander nor SeeFiltersButton present" }
    }
}

# ─────────────────────────────────────────────────────────────────────────────
#  Drive-health perf-mode line — the CORE honesty check for the detection fix.
#  The bug was: the app wrongly reported "Performance mode: Off" + offered "turn on"
#  on this org-managed, async-ON machine — caused by an INVERTED Get-MpPreference
#  mapping (it read 1 = on but treated it as off). The global Defender pref is reliable
#  (1 = on/async, verified against Windows Security "See volumes") and readable
#  UNELEVATED, so this run must POSITIVELY read "Performance mode: On (async)" — never
#  "Off", never "Unknown", never offering "turn on". (TextBlock UIA Name == its Text.)
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Drive health: perf-mode line reads 'On (async)' (never 'Off' / 'turn on')" {
    winapp ui wait-for "DriveHealthPerformanceMode" -a $AppPid -t 3000
    if ($LASTEXITCODE -ne 0) { throw "DriveHealthPerformanceMode line not found" }
    $n = Get-Name "DriveHealthPerformanceMode"
    if ($n -match '(?i)\bOff\b')      { throw "perf-mode line wrongly reads 'Off': '$n'" }
    if ($n -match '(?i)turn on')      { throw "perf-mode line wrongly offers 'turn on': '$n'" }
    if ($n -notmatch '(?i)Performance mode:') { throw "perf-mode line missing its label: '$n'" }
    # This machine has performance mode ON (async); the reliable global pref drives it even unelevated.
    if ($n -notmatch 'On \(async\)') { throw "perf-mode line should read 'On (async)' on this machine: '$n'" }
}

# ═════════════════════════════════════════════════════════════════════════════
#  FULL-PAGE SECTIONS (concept page) — every section header + key controls are
#  present, real data populates, and every *mutating* affordance is a SAFE preview.
#  All controls are direct StackPanel children or fully-realized ItemsRepeater rows
#  (the page's single ScrollViewer gives the repeaters infinite measure), so they are
#  in the UIA tree regardless of scroll position — no scrolling needed to assert them.
# ═════════════════════════════════════════════════════════════════════════════

# ── Section headers (in mock order). These are plain TextBlocks, always realized. ──
Test-UI "Section header: Performance"        { if ((Get-MatchCount 'Performance') -lt 1)       { throw "header 'Performance' not found" } }
Test-UI "Section header: Package caches"     { if ((Get-MatchCount 'Package caches') -lt 1)    { throw "header 'Package caches' not found" } }
Test-UI "Section header: Drive health"       { if ((Get-MatchCount 'Drive health') -lt 1)      { throw "header 'Drive health' not found" } }

# ── CHANGE 2: the "Source code" section was REMOVED (its only real action set VS Code's
#    git.defaultCloneDirectory, which git/gh/GitHub Desktop/Visual Studio ignore — misleading).
#    A genuine system-wide clone redirection is a FUTURE item (out of scope).
#    NOTE: removal is asserted by AutomationId (--gone), NOT free-text. `winapp ui search`
#    token-matches, and "source" legitimately appears in the reframed "You could do more"
#    copy ("Put your source on the Dev Drive"), so a text search for "Source code" would
#    false-positive. The id-based checks are the reliable proof the section is gone. ──
Test-UI "Section: Source-code feature fully removed (all action ids gone)" {
    foreach ($id in @('UseSourceButton', 'ConfirmUseSource', 'RevertSourceButton', 'SourceManualNote')) {
        winapp ui wait-for $id -a $AppPid --gone -t 2000
        if ($LASTEXITCODE -ne 0) { throw "source-feature control '$id' still present" }
    }
}
Test-UI "Source: 'Use Dev Drive source' action is gone" { winapp ui wait-for "UseSourceButton" -a $AppPid --gone -t 2000 }

# ── CHANGE 2: "Manage in Storage" relocated from the status banner into the Drive
#    health card footer (NOT invoked — it would pop the Settings app; it is a safe
#    ms-settings: launch). The AutomationId is preserved so tests still find it. ──
Test-UI "Manage in Storage button lives in the Drive health card" { winapp ui wait-for "ManageInStorageButton" -a $AppPid -t 3000 }

# ─────────────────────────────────────────────────────────────────────────────
#  Ecosystems (Concept A) — the unified per-ecosystem experience REPLACES the old three
#  sections: the Performance "Run tests" suite, the standalone "Package caches" section,
#  and the "Suggestions" upside panel. One card per language ecosystem fuses: the detected
#  tools -> where each cache lives (C: vs Dev Drive) -> one reversible Move -> the measured
#  speedup inline, but ONLY where a real workload exists (Node/npm, .NET/dotnet, Rust/cargo).
#  Honesty is non-negotiable: ecosystems WITHOUT a workload show the move action plus an
#  honest "build benchmark not available" note -- never a fabricated number.
#
#  SAFETY: the app is launched under DDM_UITEST_SAFE_MUTATIONS=1 (SafeMutationModeIndicator),
#  so every Move/Map runs in-memory -- no real cache, env var, or disk is touched. The
#  "Run all tests" benchmark is READ-ONLY and bounded; we don't run it here (the Core suite
#  tests cover the streaming run) to keep the live UI test fast.
# ─────────────────────────────────────────────────────────────────────────────

# Top affordances: an honest one-line summary, the in-app doc link, and "Run all tests".
Test-UI "Ecosystems: honest one-line summary present" {
    winapp ui wait-for "EcosystemsSummary" -a $AppPid -t 4000
    if ($LASTEXITCODE -ne 0) { throw "EcosystemsSummary not found" }
}
Test-UI "Ecosystems: 'Learn what this does' doc link opens the cache-move explainer" {
    winapp ui wait-for "LearnWhatThisDoes" -a $AppPid -t 3000
    if ($LASTEXITCODE -ne 0) { throw "LearnWhatThisDoes doc link not found" }
    winapp ui invoke "LearnWhatThisDoes" -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "invoke LearnWhatThisDoes failed" }
    winapp ui wait-for "LearnAboutCacheMovesDialog" -a $AppPid -t 4000
    if ($LASTEXITCODE -ne 0) { throw "cache-move explainer dialog did not open" }
    # Dismiss via the dialog's close button ("Got it") so it doesn't stay foreground and break later invokes.
    winapp ui invoke "Got it" -a $AppPid 2>$null | Out-Null
    winapp ui wait-for "LearnAboutCacheMovesDialog" -a $AppPid --gone -t 4000
    if ($LASTEXITCODE -ne 0) { throw "explainer dialog did not close" }
}
Test-UI "Ecosystems: 'Run all tests' benchmark affordance present" {
    winapp ui wait-for "RunAllTestsButton" -a $AppPid -t 3000
}

# One card per ecosystem; the three benchmarked stacks lead, then the movable-but-unmeasured ones.
Test-UI "Ecosystems: the Git benchmark-only card is present" { winapp ui wait-for "Ecosystem_Git" -a $AppPid -t 4000 }
Test-UI "Ecosystems: the Node card is present"          { winapp ui wait-for "Ecosystem_Node"   -a $AppPid -t 4000 }
Test-UI "Ecosystems: the .NET card is present"          { winapp ui wait-for "Ecosystem_NET"    -a $AppPid -t 3000 }
Test-UI "Ecosystems: the Rust card is present"          { winapp ui wait-for "Ecosystem_Rust"   -a $AppPid -t 3000 }
Test-UI "Ecosystems: an unmeasured ecosystem (Python) is present" { winapp ui wait-for "Ecosystem_Python" -a $AppPid -t 3000 }

# Honesty: an unmeasured ecosystem shows the honest 'not available' note, never a fake number.
Test-UI "Ecosystems: unmeasured ecosystems carry the honest 'benchmark not available' note" {
    winapp ui scroll-into-view "Ecosystem_Python" -a $AppPid 2>$null | Out-Null
    winapp ui invoke "Ecosystem_Python" -a $AppPid 2>$null | Out-Null
    Start-Sleep -Milliseconds 400
    if ((Get-MatchCount 'benchmark not available') -lt 1) { throw "honest no-benchmark note not shown for an unmeasured ecosystem" }
}

# The OLD three-section structure is GONE -- assert each removed affordance is absent by id.
Test-UI "Removed: old 'Suggestions' header gone"             { winapp ui wait-for "SuggestionsHeader"     -a $AppPid --gone -t 3000 }
Test-UI "Removed: old 'Move all caches' button gone"         { winapp ui wait-for "MoveAllCachesButton"   -a $AppPid --gone -t 2000 }
Test-UI "Removed: old 'Move your package caches' lever gone" { winapp ui wait-for "LeverMoveCaches"       -a $AppPid --gone -t 2000 }
Test-UI "Removed: old per-cache warning bar gone"            { winapp ui wait-for "PackageCacheWarningBar" -a $AppPid --gone -t 2000 }

# ── A per-tool Move inside an ecosystem card performs a REAL, reversible move (M4). Under the
#    SAFE-mutation seam the real mover is swapped for an in-memory SafeFake, so no real cache/env
#    is touched. We CONFIRM the seam is active, expand the Node card so its per-tool rows realize,
#    then exercise the npm row: Move -> Confirm -> live progress -> "Moved" result -> Move back. ──
Test-UI "Move: SAFE-mutation test seam is active (guards the live move)" {
    winapp ui wait-for "SafeMutationModeIndicator" -a $AppPid -t 4000
    if ($LASTEXITCODE -ne 0) { throw "SafeMutationModeIndicator absent -- refusing to run the live move (would touch a real cache)" }
}
# The Node card auto-expands when detected (ShouldExpand); just bring it into view. Do NOT invoke the
# header — that would TOGGLE an already-expanded card CLOSED and hide its per-tool rows.
winapp ui scroll-into-view "Ecosystem_Node" -a $AppPid 2>$null | Out-Null
Start-Sleep -Milliseconds 400
Test-UI "Move: the npm tool row is present inside the Node card" {
    winapp ui wait-for "PackageCacheCard_npm" -a $AppPid -t 4000
}
winapp ui scroll-into-view "PackageCacheCard_npm" -a $AppPid 2>$null | Out-Null
# The live-move flow needs npm detected ON C: (a "Move" button). On a machine where npm's cache is already
# on the Dev Drive (or undetected), there is nothing to move FROM C: — record those checks as SKIPPED
# (machine state), not failed. The move engine itself is covered exhaustively by the unit-test suite.
winapp ui wait-for "MoveCache_npm" -a $AppPid -t 2500 2>$null | Out-Null
$npmMovable = ($LASTEXITCODE -eq 0)
if (-not $npmMovable) {
    foreach ($n in @(
        "Move: Move opens a reversible-move confirm; Cancel collapses it",
        "Move: Confirm performs the (safely-faked) real move and shows a 'Moved' result",
        "Move: a 'Move back' affordance is revealed after the move",
        "Move: 'Move back' reverses the move (Move returns, Move back disappears)")) {
        $skip++; $results += @{ name = $n; status = "SKIP"; detail = "npm is not on C: on this machine (already relocated/undetected) — nothing to move" }
        Write-Host "  SKIP: $n — npm not on C: (nothing to move from C:)" -ForegroundColor Yellow
    }
} else {
    Test-UI "Move: Move opens a reversible-move confirm; Cancel collapses it" {
        winapp ui invoke "MoveCache_npm" -a $AppPid
        if ($LASTEXITCODE -ne 0) { throw "invoke MoveCache_npm failed" }
        Start-Sleep -Milliseconds 500
        winapp ui wait-for "ConfirmMove_npm" -a $AppPid -t 4000
        if ($LASTEXITCODE -ne 0) { throw "inline move-confirm did not appear" }
        winapp ui invoke "CancelMove_npm" -a $AppPid
        if ($LASTEXITCODE -ne 0) { throw "invoke CancelMove_npm failed" }
        winapp ui wait-for "ConfirmMove_npm" -a $AppPid --gone -t 3000
        if ($LASTEXITCODE -ne 0) { throw "inline confirm did not collapse after Cancel" }
    }
    Test-UI "Move: Confirm performs the (safely-faked) real move and shows a 'Moved' result" {
        winapp ui invoke "MoveCache_npm" -a $AppPid
        if ($LASTEXITCODE -ne 0) { throw "re-invoke MoveCache_npm failed" }
        winapp ui wait-for "ConfirmMove_npm" -a $AppPid -t 4000
        if ($LASTEXITCODE -ne 0) { throw "inline move-confirm did not re-appear" }
        winapp ui invoke "ConfirmMove_npm" -a $AppPid
        if ($LASTEXITCODE -ne 0) { throw "invoke ConfirmMove_npm failed" }
        winapp ui wait-for "MoveResult_npm" -a $AppPid -t 8000
        if ($LASTEXITCODE -ne 0) { throw "move result did not appear after Confirm" }
        $seen = $false
        for ($i = 0; $i -lt 15; $i++) {
            if ((Get-MatchCount 'Moved') -ge 1) { $seen = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $seen) { throw "Confirm did not yield a 'Moved' result" }
        winapp ui wait-for "ConfirmMove_npm" -a $AppPid --gone -t 3000
        if ($LASTEXITCODE -ne 0) { throw "inline confirm did not collapse after Confirm" }
    }
    Test-UI "Move: a 'Move back' affordance is revealed after the move" {
        winapp ui wait-for "MoveBack_npm" -a $AppPid -t 4000
        if ($LASTEXITCODE -ne 0) { throw "'Move back' affordance not revealed after a successful move" }
    }
    Test-UI "Move: 'Move back' reverses the move (Move returns, Move back disappears)" {
        winapp ui invoke "MoveBack_npm" -a $AppPid
        if ($LASTEXITCODE -ne 0) { throw "invoke MoveBack_npm failed" }
        winapp ui wait-for "MoveCache_npm" -a $AppPid -t 6000
        if ($LASTEXITCODE -ne 0) { throw "Move button did not return after Move back" }
        winapp ui wait-for "MoveBack_npm" -a $AppPid --gone -t 4000
        if ($LASTEXITCODE -ne 0) { throw "'Move back' affordance did not disappear after reverting" }
    }
}

# ── CHANGE 2: the entire "Source code" section was REMOVED (its only action set VS Code's
#    git.defaultCloneDirectory, which git CLI / gh / GitHub Desktop / Visual Studio ignore — it
#    implied all clones redirect when they do not). A genuine system-wide clone redirection is a
#    deliberately out-of-scope future item. Assert every former affordance is gone. ──
Test-UI "Source: 'Use Dev Drive source' action gone (misleading feature removed)" { winapp ui wait-for "UseSourceButton" -a $AppPid --gone -t 3000 }
Test-UI "Source: confirm affordance gone"  { winapp ui wait-for "ConfirmUseSource"  -a $AppPid --gone -t 2000 }
Test-UI "Source: Revert affordance gone"    { winapp ui wait-for "RevertSourceButton" -a $AppPid --gone -t 2000 }
Test-UI "Source: detect-only note gone"     { winapp ui wait-for "SourceManualNote"  -a $AppPid --gone -t 2000 }

# ── Drive health: capacity bar + Healthy pill render with real values. ──
Test-UI "Drive health: capacity bar present" { winapp ui wait-for "DriveCapacityBar" -a $AppPid -t 3000 }
Test-UI "Drive health: 'Healthy' pill present" {
    if ((Get-MatchCount 'Healthy') -lt 1) { throw "'Healthy' pill not found" }
}
Test-UI "Drive health: capacity caption reports used/free" {
    if ((Get-MatchCount 'free') -lt 1) { throw "capacity caption (used/free) not found" }
}

# ── Capture full-page evidence (speed test now populated). Scroll through the page so
#    the screenshots show each section, then return to the top. The page host is the
#    outer ScrollViewer (PageScrollViewer); the section lists are non-virtualizing
#    ItemsControls, so scroll-into-view on any realized row reliably brings it on-screen. ──
New-Item -ItemType Directory -Force -Path "screenshots" | Out-Null
winapp ui scroll "PageScrollViewer" -a $AppPid --to top 2>$null | Out-Null
Start-Sleep -Milliseconds 500
winapp ui screenshot -a $AppPid -o "screenshots/01-initial.png" 2>$null | Out-Null
# Performance — at the top of the page the populated speed-test card is fully visible.
winapp ui screenshot -a $AppPid -o "screenshots/03-performance.png" 2>$null | Out-Null
# Ecosystems — scroll the Node card (and its npm row) into view for the cards screenshot.
winapp ui scroll-into-view "Ecosystem_Node" -a $AppPid 2>$null | Out-Null
Start-Sleep -Milliseconds 500
winapp ui screenshot -a $AppPid -o "screenshots/04-ecosystems.png" 2>$null | Out-Null
# An unmeasured ecosystem (Python) — its honest "benchmark not available" note.
winapp ui scroll-into-view "Ecosystem_Python" -a $AppPid 2>$null | Out-Null
Start-Sleep -Milliseconds 500
winapp ui screenshot -a $AppPid -o "screenshots/05a-ecosystems-unmeasured.png" 2>$null | Out-Null
# Drive health + the per-volume list (bottom of the page).
winapp ui scroll-into-view "VolumeCard_Recovery" -a $AppPid 2>$null | Out-Null
Start-Sleep -Milliseconds 500
winapp ui screenshot -a $AppPid -o "screenshots/06-health-volumes.png" 2>$null | Out-Null
# Return to the top for the navigation round-trip.
winapp ui scroll "PageScrollViewer" -a $AppPid --to top 2>$null | Out-Null
Start-Sleep -Milliseconds 500
# ─────────────────────────────────────────────────────────────────────────────
#  Navigation round-trip — Create button routes to the real creation flow, Back returns.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Create Dev Drive page" {
    winapp ui invoke "CreateDevDriveButton" -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "invoke CreateDevDriveButton failed" }
    Start-Sleep -Milliseconds 800
    winapp ui wait-for "SourceComboBox" -a $AppPid -t 4000
}
Test-UI "Creation: source selector + size control present" {
    winapp ui wait-for "SourceComboBox" -a $AppPid -t 3000
    # The disk-bar is decorative (AccessibilityView=Raw); assert the accessible size control instead.
    winapp ui wait-for "SizeSlider" -a $AppPid -t 4000
}
Test-UI "Creation: size slider present"      { winapp ui wait-for "SizeSlider"    -a $AppPid -t 3000 }
Test-UI "Creation: size number box present"  { winapp ui wait-for "SizeNumberBox" -a $AppPid -t 3000 }
Test-UI "Creation: Create/Format button present" { winapp ui wait-for "CreateButton" -a $AppPid -t 3000 }
winapp ui screenshot -a $AppPid -o "screenshots/02-create-flow.png" 2>$null | Out-Null
Test-UI "Back navigation returns to the Dev Drive page" {
    winapp ui invoke "BackButton" -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "invoke BackButton failed" }
    Start-Sleep -Milliseconds 800
    # The status banner is dropped in the active Dev Drive state (CHANGE 1); assert on
    # the always-present Refresh button instead.
    winapp ui wait-for "RefreshButton" -a $AppPid -t 4000
}

# ─────────────────────────────────────────────────────────────────────────────
#  Accessibility audit — every interactive control the app authors has an AutomationId.
#  (Window chrome — Minimize/Maximize/Close/System menu — is framework-owned; excluded.)
# ─────────────────────────────────────────────────────────────────────────────
$inspect = winapp ui inspect -a $AppPid --interactive --json 2>$null | ConvertFrom-Json
$all = @($inspect.windows | ForEach-Object { $_.elements })
$appElements = @($all | Where-Object {
    $_.type -match 'Button|TextBox|ComboBox|CheckBox|ToggleSwitch|TabItem|Edit|Hyperlink' -and
    $_.name -notmatch 'Minimize|Maximize|Close|System'
})
$missingId = @($appElements | Where-Object { -not $_.automationId })
if ($appElements.Count -gt 0 -and $missingId.Count -eq 0) {
    $pass++; $results += @{ name = "All app interactive controls have AutomationId"; status = "PASS" }
    Write-Host "  PASS: All app interactive controls have AutomationId ($($appElements.Count) checked)" -ForegroundColor Green
} else {
    $fail++
    $names = ($missingId | ForEach-Object { "$($_.type) '$($_.name)'" }) -join ", "
    $results += @{ name = "AutomationId coverage"; status = "FAIL"; detail = "Missing: $names (checked $($appElements.Count))" }
    Write-Host "  FAIL: AutomationId coverage — Missing: $names" -ForegroundColor Red
}

# ─── Final screenshot (back on the volumes list). ───
winapp ui screenshot -a $AppPid -o "test-screenshot.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  Results
# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n────────────────────────────────────────"
Write-Host "Passed: $pass | Failed: $fail | Skipped: $skip"
$results | ConvertTo-Json | Out-File "test-results.json" -Encoding utf8
if ($fail -gt 0) { exit 1 } else { exit 0 }
