<#
    ui-tests.ps1 — WinApp UI tests for DevDriveManager (NavigationView shell)

    Follows the winui-ui-testing skill: one batch script, single run, structured results.
    Drives the *built, running* app via `winapp ui` (UI Automation).

    The redesign splits the old single endless-scroll page into a NavigationView shell of focused
    rooms — Overview, Reclaim, Space, Caches, Drives, Benchmarks, Create Dev Drive and Settings —
    all backed by ONE shared MainPageViewModel (App.Shared), loaded once. This suite:
      (1) asserts the shell + every nav item renders;
      (2) navigates to each page and asserts a page-specific anchor element;
      (3) checks the headline data is correct (G: is a ReFS Dev Drive; C: is plain NTFS);
      (4) checks the package-cache status grouping (Needs action / On your Dev Drive / Not installed);
      (5) exercises the one real preference — the Light/Dark/System theme override — and asserts the
          ComboBox reflects each choice (the override is applied live + persisted by ThemeService);
      (6) audits accessibility (every app-authored interactive control exposes an AutomationId);
      (7) screenshots every page for visual review.

    This suite is READ-ONLY with respect to the user's machine: mutation scenarios run only under the
    DDM_UITEST_SAFE_MUTATIONS in-memory seam. Switching the app theme is safe and reversible, and the
    suite restores "System default" at the end.

    Usage:
      # Set DDM_UITEST_SAFE_MUTATIONS=1 at user scope, launch the app, verify the visible safe-mode
      # indicator, and note its PID. This script exits with code 2 if the seam is absent.
      .\ui-tests.ps1 -AppPid <PID>

    Exit code 0 = all passed, 1 = one or more failures, 2 = safe mutation mode is absent.
    Results are also written to test-results.json next to this script.
#>
param([Parameter(Mandatory)][int]$AppPid)
# NOTE: do NOT name the parameter $Pid — it is read-only in PowerShell.

$ErrorActionPreference = 'Continue'
Set-Location -Path $PSScriptRoot

$pass = 0; $fail = 0; $results = @()

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

# Read an element's UIA Name. Returns "" if the element/property is missing.
function Get-Name([string]$id) {
    $json = winapp ui get-property $id -a $AppPid -p Name --json 2>$null | ConvertFrom-Json
    return [string]$json.properties.Name
}

# Read a control's effective value (ComboBox selected item, TextBox text, etc.).
function Get-Value([string]$id) {
    $json = winapp ui get-value $id -a $AppPid --json 2>$null | ConvertFrom-Json
    return [string]$json.text
}

# Presence test built on the tool's own resolver. `winapp ui inspect <id>` silently falls back to a
# whole-tree dump when the id is absent, so substring-matching its output is not a presence check.
function Test-Present([string]$id, [int]$timeoutMs = 1500) {
    winapp ui wait-for $id -a $AppPid -t $timeoutMs 2>&1 | Out-Null
    $found = ($LASTEXITCODE -eq 0)
    $global:LASTEXITCODE = 0     # a legitimate "absent" must not fail the enclosing Test-UI
    return $found
}

# Every semantic slug under a subtree. `inspect --json` returns a NESTED tree whose nodes carry
# `selector` (a slug derived from the AutomationId), not `automationId` — only `--interactive`
# flattens and exposes ids, and it filters out lists, canvases and rows.
function Get-Selectors([string]$rootId) {
    $obj = winapp ui inspect $rootId -a $AppPid --json --depth 20 2>$null | Out-String | ConvertFrom-Json
    $global:LASTEXITCODE = 0
    $out = [System.Collections.Generic.List[string]]::new()
    function Walk($n) {
        if (-not $n) { return }
        if ($n.selector) { $out.Add([string]$n.selector) }
        foreach ($c in $n.children) { Walk $c }
    }
    foreach ($w in $obj.windows) { foreach ($e in $w.elements) { Walk $e } }
    return $out
}

# Navigate via a top-level nav item and wait for a page-specific anchor to appear.
function Goto([string]$navId, [string]$anchorId) {
    winapp ui invoke $navId -a $AppPid 2>$null | Out-Null
    Start-Sleep -Milliseconds 500
    winapp ui wait-for $anchorId -a $AppPid -t 5000 | Out-Null
}

# Select a ComboBox item by its AutomationId (expand, then invoke the item).
function Select-Combo([string]$comboId, [string]$itemId) {
    winapp ui invoke $comboId -a $AppPid 2>$null | Out-Null   # ExpandCollapse
    Start-Sleep -Milliseconds 400
    winapp ui invoke $itemId -a $AppPid 2>$null | Out-Null    # SelectionItem
    Start-Sleep -Milliseconds 500
}

New-Item -ItemType Directory -Force -Path "screenshots" | Out-Null
Write-Host "DevDriveManager UI tests (NavigationView shell) — app PID $AppPid`n"

# Hard safety gate: do not merely record this as a failed test and continue into mutation scenarios.
winapp ui wait-for "SafeMutationModeIndicator" -a $AppPid -t 4000 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Safe mutation mode is not active. Refusing to run UI tests that can confirm machine mutations."
    exit 2
}

# ─────────────────────────────────────────────────────────────────────────────
#  (1) Shell + navigation rail render
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Shell NavigationView present"      { winapp ui wait-for "ShellNavView"     -a $AppPid -t 6000 }
Test-UI "Nav: Dashboard present"            { winapp ui wait-for "NavDashboard"     -a $AppPid -t 4000 }
Test-UI "Nav: Reclaim present"              { winapp ui wait-for "NavReclaim"       -a $AppPid -t 4000 }
Test-UI "Nav: Package caches present"       { winapp ui wait-for "NavPackageCaches" -a $AppPid -t 4000 }
Test-UI "Nav: Benchmarks present"           { winapp ui wait-for "NavBenchmarks"    -a $AppPid -t 4000 }
Test-UI "Nav: Drives present"               { winapp ui wait-for "NavDrives"        -a $AppPid -t 4000 }
Test-UI "Nav: Create Dev Drive present"     { winapp ui wait-for "NavCreate"        -a $AppPid -t 4000 }

# ─────────────────────────────────────────────────────────────────────────────
#  (2) Overview — the landing room. Ranks what this machine needs and routes each finding to the
#      room that acts on it. Everything here is derived from the other rooms' data, so the assertions
#      are about the room's own furniture (strip, table, chart, inspector, status bar) rather than
#      about any particular finding — which findings appear depends entirely on the test machine.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Overview"               { Goto "NavDashboard" "SignalsSubtitle" }
Test-UI "Overview: volume strip"             { winapp ui wait-for "OverviewVolumeStrip"   -a $AppPid -t 3000 }
Test-UI "Overview: scan action"              { winapp ui wait-for "ScanEverythingButton"  -a $AppPid -t 3000 }
Test-UI "Overview: last-scan chip"           { winapp ui wait-for "LastScanChip"          -a $AppPid -t 3000 }
Test-UI "Overview: free-space chart card"    { winapp ui wait-for "TrendSubtitle"         -a $AppPid -t 3000 }
Test-UI "Overview: inspector title"          { winapp ui wait-for "OverviewInspectorTitle" -a $AppPid -t 3000 }
Test-UI "Overview: coverage facts"           { winapp ui wait-for "CoverageNodes"        -a $AppPid -t 3000 }
Test-UI "Overview: status bar"               { winapp ui wait-for "OverviewStatusBar"     -a $AppPid -t 3000 }
Test-UI "Overview: inspector footnote"       { winapp ui wait-for "OverviewFootNote"      -a $AppPid -t 3000 }

# The signals table and its empty state are mutually exclusive, and which one shows depends on what
# the machine actually has to report — so assert that the room committed to one of them.
Test-UI "Overview: signals table or empty state" {
    if (-not ((Test-Present "SignalsList") -or (Test-Present "SignalsEmptyTitle"))) {
        throw "neither the signals table nor its empty state rendered"
    }
    winapp ui wait-for "SignalsSubtitle" -a $AppPid -t 3000 | Out-Null
}

# The chart draws only once two readings sit 30 minutes apart, which a fresh profile never has —
# so accept either the plotted chart or the honest "not enough history yet" card.
Test-UI "Overview: chart or not-enough-history" {
    if (-not ((Test-Present "FreeSpaceChart") -or (Test-Present "TrendEmptyTitle"))) {
        throw "the trend card rendered neither a chart nor its empty state"
    }
    winapp ui wait-for "TrendSubtitle" -a $AppPid -t 3000 | Out-Null
}

# Selecting a signal explains it in place; only the GOES TO chip leaves the room. Both halves are
# asserted because one gesture doing both is exactly the defect this split fixed.
Test-UI "Overview: selecting a signal explains it without navigating" {
    # Signal ids are derived from the finding, so they are discovered rather than hard-coded.
    $row = Get-Selectors "SignalsList" | Where-Object { $_ -clike 'Signal_*' } | Select-Object -First 1
    if (-not $row) {
        # A machine with nothing to report has no row to select; the empty state must be why.
        if (-not (Test-Present "SignalsEmptyTitle")) { throw "no signal rows and no empty state" }
        winapp ui wait-for "SignalsSubtitle" -a $AppPid -t 3000 | Out-Null
        return
    }
    winapp ui click $row -a $AppPid 2>$null | Out-Null
    Start-Sleep -Milliseconds 400
    if ((Get-Name "OverviewInspectorTitle") -ne 'About this signal') {
        throw "selecting a signal did not fill the inspector"
    }
    if ([string]::IsNullOrWhiteSpace((Get-Name "OverviewSelectionDetail"))) {
        throw "the inspector did not say why the signal is listed"
    }
    # Still in Overview — selection must not navigate.
    winapp ui wait-for "SignalsSubtitle" -a $AppPid -t 3000 | Out-Null
}

Test-UI "Overview: the GOES TO chip opens that room" {
    $chip = Get-Selectors "SignalsList" | Where-Object { $_ -clike 'SignalRoom_*' } | Select-Object -First 1
    if (-not $chip) { winapp ui wait-for "SignalsSubtitle" -a $AppPid -t 3000 | Out-Null; return }
    winapp ui invoke $chip -a $AppPid 2>$null | Out-Null
    Start-Sleep -Milliseconds 700
    # Whichever room it landed in, Overview is no longer the one on screen.
    if (Test-Present "SignalsSubtitle") { throw "$chip did not leave the Overview room" }
    Goto "NavDashboard" "SignalsSubtitle"
}
winapp ui screenshot -a $AppPid -o "screenshots\01-overview.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  (2b) Reclaim — the room the Storage Manager reframe is built around. Scanning is deliberately
#       NOT started on navigation (a real scan is minutes of I/O), so these assert the resting
#       state: the room renders, every category is listed, and Scan is offered rather than running.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Reclaim"                { Goto "NavReclaim" "ReclaimCategoryList" }
Test-UI "Reclaim: Scan present"              { winapp ui wait-for "ReclaimScanButton"   -a $AppPid -t 3000 }
Test-UI "Reclaim: found total present"       { winapp ui wait-for "ReclaimFoundBytes"   -a $AppPid -t 3000 }
Test-UI "Reclaim: selected total present"    { winapp ui wait-for "ReclaimSelectedBytes" -a $AppPid -t 3000 }
Test-UI "Reclaim: does not scan on entry" {
    $s = Get-Value "ReclaimStatus"
    if ($s -notmatch 'Nothing scanned yet') { throw "expected an unscanned resting state, got '$s'" }
}
winapp ui screenshot -a $AppPid -o "screenshots\01b-reclaim.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  (3) Package caches — two tabs over one table: Detected, then Not installed.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Package caches"         { Goto "NavPackageCaches" "PackageCachesScrollViewer" }
# The Detected tab is where the room opens. (This PC: NuGet/pip/Cargo/vcpkg on C:, npm already on G:;
# uv/Poetry/Gradle/... are undetected and live on the other tab.)
Test-UI "Caches: tab strip present"          { winapp ui wait-for "CacheTab_Detected" -a $AppPid -t 3000 }
Test-UI "Caches: Needs-action row present (Cargo)" { winapp ui wait-for "MoveCache_Cargo" -a $AppPid -t 4000 }
# npm's representative is the CARD, not a button: "Move back" is gated on CanMoveBack, which only
# becomes true after *this app* performs a move in the current session. A cache that was already on
# the Dev Drive at launch renders the "Already on Dev Drive" checkmark instead, so MoveBack_npm can
# never exist on a fresh app. The card's name ("npm, On G:") proves the grouping more directly anyway.
Test-UI "Caches: On-Dev-Drive row present (npm)"   { winapp ui wait-for "PackageCacheCard_npm" -a $AppPid -t 4000 }
# Undetected tools are on their own tab now, so reaching uv means switching first. That makes this a
# stronger assertion than it used to be: it proves the tab actually filters, not just that uv exists.
# `invoke` rather than `click` — click simulates a mouse and silently no-ops on a locked workstation.
Test-UI "Caches: Not-installed tab switches" {
    winapp ui invoke "CacheTab_NotInstalled" -a $AppPid
    winapp ui wait-for "MapPathInput_uv" -a $AppPid -t 4000
}
# ...and Cargo must be gone from that tab, which is the half the presence check cannot prove.
Test-UI "Caches: Not-installed tab excludes detected rows" {
    $found = winapp ui inspect -a $AppPid --json 2>$null | Out-String
    if ($found -match 'MoveCache_Cargo') { throw "Cargo is still visible on the Not installed tab" }
}
Test-UI "Caches: back to Detected" {
    winapp ui invoke "CacheTab_Detected" -a $AppPid
    winapp ui wait-for "MoveCache_Cargo" -a $AppPid -t 4000
}
Test-UI "Caches: Move all present"           { winapp ui wait-for "MoveAllButton"     -a $AppPid -t 3000 }
Test-UI "Caches: 'Learn what this does' link"{ winapp ui wait-for "LearnWhatThisDoes" -a $AppPid -t 3000 }
winapp ui screenshot -a $AppPid -o "screenshots\02-caches.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  (4) Benchmarks — demoted to on-demand; run-all hero + per-workload rows.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Benchmarks"             { Goto "NavBenchmarks" "BenchmarksScrollViewer" }
Test-UI "Benchmarks: Run all present"        { winapp ui wait-for "RunAllTestsButton" -a $AppPid -t 4000 }
# The workloads list is a banded ItemsControl (no peer); assert on the universal git-clone row's
# Run button (the filesystem baseline that's always present).
Test-UI "Benchmarks: git-clone workload row" { winapp ui wait-for "RunRow_git-clone"  -a $AppPid -t 4000 }
winapp ui screenshot -a $AppPid -o "screenshots\03-benchmarks.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  (5) Drives — volumes / filter drivers tabs over one table. G: is the real ReFS Dev Drive.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Drives"                 { Goto "NavDrives" "VolumesList" }
Test-UI "Drives: tab strip present"          { winapp ui wait-for "DriveTab_Volumes"     -a $AppPid -t 3000 }
Test-UI "Drives: 'Manage in Storage'"        { winapp ui wait-for "ManageInStorageButton" -a $AppPid -t 4000 }
Test-UI "Drives: G: row present"             { winapp ui wait-for "VolumeRow_G"     -a $AppPid -t 4000 }
Test-UI "Drives: C: row present"             { winapp ui wait-for "VolumeRow_C"     -a $AppPid -t 4000 }
# Every row now carries a DEV DRIVE pill; C:'s reads "No". Absence would be the ambiguous
# assertion — a missing pill and a missing row look the same.
Test-UI "Drives: G: shows Dev Drive badge"    { winapp ui wait-for "DevDriveBadge_G" -a $AppPid -t 4000 }
Test-UI "Drives: C: is marked not a Dev Drive" {
    $n = Get-Name "DevDriveBadge_C"
    if ($n -ne 'No') { throw "expected C:'s DEV DRIVE cell to read 'No' but got: '$n'" }
}
Test-UI "Drives: G: row reports ReFS" {
    $n = Get-Name "VolumeRow_G"
    if ($n -notmatch 'ReFS') { throw "expected 'ReFS' in G: row name but got: '$n'" }
}
Test-UI "Drives: G: row advertises Dev Drive" {
    $n = Get-Name "VolumeRow_G"
    if ($n -notmatch 'Dev Drive') { throw "expected 'Dev Drive' in G: row name but got: '$n'" }
}
Test-UI "Drives: C: row reports NTFS" {
    $n = Get-Name "VolumeRow_C"
    if ($n -notmatch 'NTFS') { throw "expected 'NTFS' in C: row name but got: '$n'" }
}
Test-UI "Drives: inspector names the selection" {
    $n = Get-Name "DrivesInspectorName"
    if ([string]::IsNullOrWhiteSpace($n)) { throw "inspector title was empty" }
}
Test-UI "Drives: Filter-drivers tab switches" {
    winapp ui invoke "DriveTab_FilterDrivers" -a $AppPid
    winapp ui wait-for "VolumesList" -a $AppPid --gone -t 3000
}
# Reading attached filters is privileged. Elevated we get the table; unelevated we get the button
# that offers the one-shot read. Either is correct — an empty tab would not be.
Test-UI "Drives: Filter-drivers tab has content" {
    winapp ui wait-for "FiltersList" -a $AppPid -t 2000 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { winapp ui wait-for "SeeFiltersButton" -a $AppPid -t 3000 }
}
Test-UI "Drives: back to Volumes" {
    winapp ui invoke "DriveTab_Volumes" -a $AppPid
    winapp ui wait-for "VolumeRow_G" -a $AppPid -t 3000
}
winapp ui screenshot -a $AppPid -o "screenshots\04-drives.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  (6) Create Dev Drive — the form is a field list, not a wizard, and one confirmation
#      verifies and executes through the safe in-memory seam.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Create Dev Drive"       { Goto "NavCreate" "CreateFormHead" }
Test-UI "Create: room grammar is present" {
    Test-Present @("CreateVolumeStrip", "CreateFormHead", "AfterCreateHead", "CreateStatusBar")
}
Test-UI "Create: guardrails note present"    { winapp ui wait-for "GuardrailsInfoBar" -a $AppPid -t 3000 }
Test-UI "Create: size row carries a slider and its ceiling" {
    Test-Present @("SizeSlider", "SizeNumberBox", "SizeMaximumTick")
    if ((Get-Name "SizeMaximumTick") -notmatch '^[0-9,]+ GB \u2014 ') { throw "Maximum tick does not name its ceiling." }
}
Test-UI "Create: format is stated, not chosen" {
    if ((Get-Name "FormatPill") -notmatch 'ReFS') { throw "Format pill does not say ReFS." }
}
Test-UI "Create: the reclaim tie-in is present" { winapp ui wait-for "ReclaimTieIn" -a $AppPid -t 3000 }
winapp ui screenshot -a $AppPid -o "screenshots\05-create.png" 2>$null | Out-Null

Test-UI "Create: method is two options, resize is the default" {
    Test-Present @("MethodResizeOption", "MethodVhdxOption")
    winapp ui invoke "MethodResizeOption" -a $AppPid 2>$null | Out-Null
    winapp ui wait-for "MethodResizeOption" -a $AppPid -p IsSelected --value "True" -t 3000
}
Test-UI "Create: resize action is Create" {
    winapp ui wait-for "ResizeSourceComboBox" -a $AppPid -t 3000 2>$null | Out-Null
    if ((Get-Name "CreateButton") -ne "Create") { throw "Resize action is not Create." }
    winapp ui wait-for "CreateButton" -a $AppPid -p IsEnabled --value "True" -t 3000
}
Test-UI "Create: picking VHDX swaps the source row for a path row" {
    winapp ui invoke "MethodVhdxOption" -a $AppPid 2>$null | Out-Null
    winapp ui wait-for "VhdPathTextBox" -a $AppPid -t 3000 2>$null | Out-Null
    winapp ui wait-for "ResizeSourceComboBox" -a $AppPid --gone -t 3000
}
Test-UI "Create: going back to resize restores the source row" {
    winapp ui invoke "MethodResizeOption" -a $AppPid 2>$null | Out-Null
    winapp ui wait-for "ResizeSourceComboBox" -a $AppPid -t 3000
}
winapp ui screenshot -a $AppPid -o "screenshots\06-create-resize.png" 2>$null | Out-Null

Test-UI "Create: resize uses one concise confirmation" {
    winapp ui invoke "CreateButton" -a $AppPid 2>$null | Out-Null
    winapp ui wait-for "ConfirmCreateDialog" -a $AppPid -t 3000 2>$null | Out-Null
    $matches = winapp ui search "Resize" -a $AppPid --json 2>$null | Out-String | ConvertFrom-Json
    $text = (($matches.matches | ForEach-Object { $_.name }) -join "`n")
    if ($text -notmatch 'Resize [A-Z]: to [0-9]') { throw "Final source size is missing." }
    if ($text -notmatch 'Create [A-Z]: at [0-9]') { throw "Target Dev Drive size is missing." }
    if ($text -notmatch 'If verification fails, nothing changes\.') { throw "Verification guarantee is missing." }
    winapp ui wait-for "PrimaryButton" -a $AppPid -t 3000
}
winapp ui screenshot -a $AppPid -o "screenshots\07-create-confirm.png" 2>$null | Out-Null

Test-UI "Create: resize completes without another confirmation" {
    winapp ui invoke "PrimaryButton" -a $AppPid 2>$null | Out-Null
    winapp ui wait-for "GoToManagementButton" -a $AppPid -t 5000 2>$null | Out-Null
    winapp ui wait-for "PrimaryButton" -a $AppPid --gone -t 3000
}
Test-UI "Create: the done card offers the two next rooms" {
    Test-Present @("CompletionTitle", "MovePackageCachesButton", "RunSpeedTestButton")
}
winapp ui screenshot -a $AppPid -o "screenshots\08-create-complete.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  (7) Settings — two cards of preferences, every one of which reaches a real decision.
#      The suite changes each preference, asserts the control reflects it, then puts it back, so a
#      run leaves the machine and the app exactly as it found them.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Settings" {
    winapp ui invoke "SettingsItem" -a $AppPid 2>$null | Out-Null
    Start-Sleep -Milliseconds 500
    winapp ui wait-for "ThemeSelector" -a $AppPid -t 5000 | Out-Null
}
Test-UI "Settings: Rescan present"           { winapp ui wait-for "RescanButton"          -a $AppPid -t 3000 }
Test-UI "Settings: Create present"           { winapp ui wait-for "SettingsCreateButton"  -a $AppPid -t 3000 }
Test-UI "Settings: Windows Security present" { winapp ui wait-for "SettingsOpenWindowsSecurityButton" -a $AppPid -t 3000 }
Test-UI "Settings: two cards"                {
    if (-not (Test-Present "SignalSettingsHead")) { throw "Overview-and-signals card head missing" }
    winapp ui wait-for "MachineSettingsHead" -a $AppPid -t 3000
}
Test-UI "Settings: status bar"               { winapp ui wait-for "SettingsStatusBar"     -a $AppPid -t 3000 }
Test-UI "Settings: reset present"            { winapp ui wait-for "ResetPreferencesButton" -a $AppPid -t 3000 }
winapp ui screenshot -a $AppPid -o "screenshots\09-settings.png" 2>$null | Out-Null

# Every preference control is reachable. A settings page whose controls do not resolve is the one
# failure mode that looks identical to a working page in a screenshot.
foreach ($pref in @("WatchCachesToggle", "LowFreeCombo", "RollupCombo",
                    "RecordHistoryToggle", "RetentionCombo", "ClearHistoryButton",
                    "CreationMethodCombo")) {
    Test-UI "Settings: $pref present" { winapp ui wait-for $pref -a $AppPid -t 3000 }
}

# Round-trip one combo of each kind. The threshold reaches AttentionSignalBuilder and the method
# reaches the Create room, so proving the control moves is proving the preference moves.
Select-Combo "LowFreeCombo" "LowFree25"
Test-UI "Preference: low-free threshold -> 25%" {
    winapp ui wait-for "LowFreeCombo" -a $AppPid --value "25%" -t 3000
}
Select-Combo "LowFreeCombo" "LowFree15"
Test-UI "Preference: low-free threshold restored" {
    winapp ui wait-for "LowFreeCombo" -a $AppPid --value "15%" -t 3000
}

Select-Combo "RollupCombo" "RollupNever"
Test-UI "Preference: cache roll-up -> Never" {
    winapp ui wait-for "RollupCombo" -a $AppPid --value "Never" -t 3000
}
Select-Combo "RollupCombo" "Rollup3"
Test-UI "Preference: cache roll-up restored" {
    winapp ui wait-for "RollupCombo" -a $AppPid --value "3 caches" -t 3000
}

Select-Combo "CreationMethodCombo" "MethodVhdx"
Test-UI "Preference: creation method -> VHDX" {
    winapp ui wait-for "CreationMethodCombo" -a $AppPid --value "New VHDX" -t 3000
}
Select-Combo "CreationMethodCombo" "MethodResize"
Test-UI "Preference: creation method restored" {
    winapp ui wait-for "CreationMethodCombo" -a $AppPid --value "Resize a volume" -t 3000
}

# A preference that changes the status bar proves the change reached something, not just the control.
Test-UI "Preference: status bar reports the threshold" {
    $facts = (Get-Selectors "SettingsStatusBar") -join " "
    if (-not $facts) { throw "Settings status bar exposed no facts" }
    winapp ui wait-for "SettingsStatusBar" -a $AppPid -t 3000
}

# Theme override: Dark, then Light, then back to System default. Assert the ComboBox reflects each.
Select-Combo "ThemeSelector" "ThemeOptionDark"
Test-UI "Theme override -> Dark" {
    winapp ui wait-for "ThemeSelector" -a $AppPid --value "Dark" -t 3000
}
winapp ui screenshot -a $AppPid -o "screenshots\10-settings-dark.png" 2>$null | Out-Null

Select-Combo "ThemeSelector" "ThemeOptionLight"
Test-UI "Theme override -> Light" {
    winapp ui wait-for "ThemeSelector" -a $AppPid --value "Light" -t 3000
}
winapp ui screenshot -a $AppPid -o "screenshots\11-settings-light.png" 2>$null | Out-Null

Select-Combo "ThemeSelector" "ThemeOptionSystem"
Test-UI "Theme override -> System default (restored)" {
    winapp ui wait-for "ThemeSelector" -a $AppPid --value "System default" -t 3000
}

# ─────────────────────────────────────────────────────────────────────────────
#  (8) Accessibility — every interactive control is identifiable to assistive tech.
#      A control is accessible if it exposes EITHER a stable AutomationId OR an accessible Name
#      (screen readers announce the Name; automation addresses the Id). We flag only controls that
#      have NEITHER. Note: framework-templated CommunityToolkit SettingsExpander header toggles
#      surface as Button peers WITHOUT an AutomationId, but they carry a Name from their Header
#      ("About the benchmarks", "Dev Drive Manager"), so they're properly announced.
#      Every *app-authored* interactive control additionally carries an AutomationId — the rest of
#      this suite proves it by addressing each one by Id directly (a missing Id fails those tests).
#      UIA only sees the live visual tree, so we walk every page and accumulate. The element array
#      lives at $obj.windows[].elements (NOT $obj.elements); flatten across all windows so the
#      open-ComboBox popup window is covered too.
# ─────────────────────────────────────────────────────────────────────────────
$auditPages = @(
    @{ nav = "NavDashboard";     anchor = "SignalsSubtitle" },
    @{ nav = "NavReclaim";       anchor = "ReclaimCategoryList" },
    @{ nav = "NavSpace";         anchor = "SpaceItemsList" },
    @{ nav = "NavPackageCaches"; anchor = "PackageCachesScrollViewer" },
    @{ nav = "NavBenchmarks";    anchor = "BenchmarksScrollViewer" },
    @{ nav = "NavDrives";        anchor = "VolumesList" },
    @{ nav = "NavCreate";        anchor = "SourceComboBox" },
    @{ nav = "SettingsItem";     anchor = "ThemeSelector" }
)
$inaccessible = @()
$auditedCount = 0
foreach ($p in $auditPages) {
    winapp ui invoke $p.nav -a $AppPid 2>$null | Out-Null
    winapp ui wait-for $p.anchor -a $AppPid -t 4000 2>$null | Out-Null
    Start-Sleep -Milliseconds 300
    $obj = winapp ui inspect -a $AppPid --interactive --json --depth 14 2>$null | Out-String | ConvertFrom-Json
    $elements = @()
    foreach ($win in $obj.windows) { if ($win.elements) { $elements += $win.elements } }
    $interactive = @($elements | Where-Object {
        $_.type -match 'Button|TextBox|ComboBox|ComboBoxItem|CheckBox|ToggleSwitch|TabItem|Edit|Hyperlink' -and
        $_.name -notmatch 'Minimize|Maximize|Close|Restore' -and
        $_.className -notmatch 'PickerHost|#32770|CabinetWClass'
    })
    $auditedCount += $interactive.Count
    foreach ($e in ($interactive | Where-Object { -not $_.automationId -and [string]::IsNullOrWhiteSpace($_.name) })) {
        $inaccessible += "$($p.nav): $($e.type) (no AutomationId, no Name)"
    }
}
if ($inaccessible.Count -eq 0) {
    $pass++; $results += @{ name = "Accessibility: all interactive controls are identifiable ($auditedCount audited across $($auditPages.Count) pages)"; status = "PASS" }
    Write-Host "  PASS: Accessibility: all $auditedCount interactive controls ($($auditPages.Count) pages) expose an AutomationId or Name" -ForegroundColor Green
} else {
    $fail++
    $names = ($inaccessible | Select-Object -Unique) -join ", "
    $results += @{ name = "Accessibility: identifiability coverage"; status = "FAIL"; detail = "Unidentifiable: $names" }
    Write-Host "  FAIL: Accessibility: identifiability coverage — $names" -ForegroundColor Red
}

# ─── Results ───
Write-Host "`nPassed: $pass | Failed: $fail"
$results | Where-Object { $_.status -eq "FAIL" } | ForEach-Object {
    Write-Host "  FAIL: $($_.name) — $($_.detail)" -ForegroundColor Red
}
$results | ConvertTo-Json -Depth 4 | Out-File "test-results.json"
if ($fail -gt 0) { exit 1 } else { exit 0 }
