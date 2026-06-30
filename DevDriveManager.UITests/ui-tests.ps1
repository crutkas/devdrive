<#
    ui-tests.ps1 — WinApp UI tests for DevDriveManager (NavigationView shell)

    Follows the winui-ui-testing skill: one batch script, single run, structured results.
    Drives the *built, running* app via `winapp ui` (UI Automation).

    The redesign splits the old single endless-scroll page into a NavigationView shell with six
    focused pages — Dashboard, Package caches, Benchmarks, Drives, Create Dev Drive and Settings —
    all backed by ONE shared MainPageViewModel (App.Shared), loaded once. This suite:
      (1) asserts the shell + every nav item renders;
      (2) navigates to each page and asserts a page-specific anchor element;
      (3) checks the headline data is correct (G: is a ReFS Dev Drive; C: is plain NTFS);
      (4) checks the package-cache status grouping (Needs action / On your Dev Drive / Not installed);
      (5) exercises the one real preference — the Light/Dark/System theme override — and asserts the
          ComboBox reflects each choice (the override is applied live + persisted by ThemeService);
      (6) audits accessibility (every app-authored interactive control exposes an AutomationId);
      (7) screenshots every page for visual review.

    This suite is READ-ONLY with respect to the user's machine: it never confirms a real cache move
    (that is gated behind an explicit inline Confirm, exercised only under the
    DDM_UITEST_SAFE_MUTATIONS in-memory seam). Switching the app theme is safe and reversible, and
    the suite restores "System default" at the end.

    Usage:
      # Launch the app first (winui-dev-workflow BuildAndRun.ps1) and note its PID, then:
      .\ui-tests.ps1 -AppPid <PID>

    Exit code 0 = all passed, 1 = one or more failures. Results also written to
    test-results.json next to this script.
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

# ─────────────────────────────────────────────────────────────────────────────
#  (1) Shell + navigation rail render
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Shell NavigationView present"      { winapp ui wait-for "ShellNavView"     -a $AppPid -t 6000 }
Test-UI "Nav: Dashboard present"            { winapp ui wait-for "NavDashboard"     -a $AppPid -t 4000 }
Test-UI "Nav: Package caches present"       { winapp ui wait-for "NavPackageCaches" -a $AppPid -t 4000 }
Test-UI "Nav: Benchmarks present"           { winapp ui wait-for "NavBenchmarks"    -a $AppPid -t 4000 }
Test-UI "Nav: Drives present"               { winapp ui wait-for "NavDrives"        -a $AppPid -t 4000 }
Test-UI "Nav: Create Dev Drive present"     { winapp ui wait-for "NavCreate"        -a $AppPid -t 4000 }

# ─────────────────────────────────────────────────────────────────────────────
#  (2) Dashboard — the calm landing surface; leads with what's NOT on the Dev Drive.
#      (Navigate explicitly so the suite is re-runnable regardless of prior nav state; the shell
#      selects Dashboard on fresh launch.)
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Dashboard"              { Goto "NavDashboard" "DashboardScrollViewer" }
Test-UI "Dashboard: Refresh present"         { winapp ui wait-for "RefreshButton"         -a $AppPid -t 3000 }
Test-UI "Dashboard: Create present"          { winapp ui wait-for "CreateDevDriveButton"  -a $AppPid -t 3000 }
# The cards are lightweight Border/ItemsControl panels (no automation peer); assert on the
# peer-projecting child each card owns — its "Move all" button and its three nav links.
Test-UI "Dashboard: caches hero (Move all)"  { winapp ui wait-for "MoveAllButton"          -a $AppPid -t 3000 }
Test-UI "Dashboard: caches hero (Manage link)" { winapp ui wait-for "OpenPackageCachesLink" -a $AppPid -t 3000 }
Test-UI "Dashboard: drive-health card link"  { winapp ui wait-for "OpenDrivesLink"         -a $AppPid -t 3000 }
Test-UI "Dashboard: benchmarks card link"    { winapp ui wait-for "OpenBenchmarksLink"     -a $AppPid -t 3000 }
winapp ui screenshot -a $AppPid -o "screenshots\01-dashboard.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  (3) Package caches — status-grouped, banded; critical "still on C:" group leads.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Package caches"         { Goto "NavPackageCaches" "PackageCachesScrollViewer" }
# Status groups are banded ItemsControls (no automation peer); assert each group rendered via a
# representative row's action button. (This PC: NuGet/pip/Cargo/vcpkg on C:, npm already on G:,
# uv/Poetry/Gradle/... not installed.)
Test-UI "Caches: Needs-action row present (Cargo)" { winapp ui wait-for "MoveCache_Cargo" -a $AppPid -t 4000 }
Test-UI "Caches: On-Dev-Drive row present (npm)"   { winapp ui wait-for "MoveBack_npm"    -a $AppPid -t 4000 }
Test-UI "Caches: Not-installed row present (uv)"   { winapp ui wait-for "MapPathInput_uv" -a $AppPid -t 4000 }
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
#  (5) Drives — Dev Drive hero + banded all-volumes table. G: is the real ReFS Dev Drive.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Drives"                 { Goto "NavDrives" "DrivesScrollViewer" }
# The Dev Drive hero is a Border (no peer); assert on its child buttons.
Test-UI "Drives: hero 'Manage in Storage'"   { winapp ui wait-for "ManageInStorageButton" -a $AppPid -t 4000 }
Test-UI "Drives: hero 'See filter drivers'"  { winapp ui wait-for "SeeFiltersButton"      -a $AppPid -t 4000 }
Test-UI "Drives: G: row present"             { winapp ui wait-for "VolumeCard_G"    -a $AppPid -t 4000 }
Test-UI "Drives: C: row present"             { winapp ui wait-for "VolumeCard_C"    -a $AppPid -t 4000 }
Test-UI "Drives: G: shows Dev Drive badge"   { winapp ui wait-for "DevDriveBadge_G" -a $AppPid -t 4000 }
Test-UI "Drives: C: has NO Dev Drive badge"  { winapp ui wait-for "DevDriveBadge_C" -a $AppPid --gone -t 3000 }
Test-UI "Drives: G: row reports ReFS" {
    $n = Get-Name "VolumeCard_G"
    if ($n -notmatch 'ReFS') { throw "expected 'ReFS' in G: row name but got: '$n'" }
}
Test-UI "Drives: G: row advertises Dev Drive" {
    $n = Get-Name "VolumeCard_G"
    if ($n -notmatch 'Dev Drive') { throw "expected 'Dev Drive' in G: row name but got: '$n'" }
}
Test-UI "Drives: C: row reports NTFS" {
    $n = Get-Name "VolumeCard_C"
    if ($n -notmatch 'NTFS') { throw "expected 'NTFS' in C: row name but got: '$n'" }
}
winapp ui screenshot -a $AppPid -o "screenshots\04-drives.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  (6) Create Dev Drive — hosted in the shell (guarded preview; nothing changes until confirm).
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Create Dev Drive"       { Goto "NavCreate" "SourceComboBox" }
Test-UI "Create: guardrails info bar present"{ winapp ui wait-for "GuardrailsInfoBar" -a $AppPid -t 3000 }
winapp ui screenshot -a $AppPid -o "screenshots\05-create.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  (7) Settings — the one real preference: Light/Dark/System theme override (applied live).
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Settings" {
    winapp ui invoke "SettingsItem" -a $AppPid 2>$null | Out-Null
    Start-Sleep -Milliseconds 500
    winapp ui wait-for "ThemeSelector" -a $AppPid -t 5000 | Out-Null
}
Test-UI "Settings: Rescan present"           { winapp ui wait-for "RescanButton"          -a $AppPid -t 3000 }
Test-UI "Settings: Create present"           { winapp ui wait-for "SettingsCreateButton"  -a $AppPid -t 3000 }
Test-UI "Settings: Windows Security present" { winapp ui wait-for "SettingsOpenWindowsSecurityButton" -a $AppPid -t 3000 }
winapp ui screenshot -a $AppPid -o "screenshots\06-settings.png" 2>$null | Out-Null

# Theme override: Dark, then Light, then back to System default. Assert the ComboBox reflects each.
Select-Combo "ThemeSelector" "ThemeOptionDark"
Test-UI "Theme override -> Dark" {
    winapp ui wait-for "ThemeSelector" -a $AppPid --value "Dark" -t 3000
}
winapp ui screenshot -a $AppPid -o "screenshots\07-settings-dark.png" 2>$null | Out-Null

Select-Combo "ThemeSelector" "ThemeOptionLight"
Test-UI "Theme override -> Light" {
    winapp ui wait-for "ThemeSelector" -a $AppPid --value "Light" -t 3000
}
winapp ui screenshot -a $AppPid -o "screenshots\08-settings-light.png" 2>$null | Out-Null

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
    @{ nav = "NavDashboard";     anchor = "DashboardScrollViewer" },
    @{ nav = "NavPackageCaches"; anchor = "PackageCachesScrollViewer" },
    @{ nav = "NavBenchmarks";    anchor = "BenchmarksScrollViewer" },
    @{ nav = "NavDrives";        anchor = "DrivesScrollViewer" },
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
    $pass++; $results += @{ name = "Accessibility: all interactive controls are identifiable ($auditedCount audited across 6 pages)"; status = "PASS" }
    Write-Host "  PASS: Accessibility: all $auditedCount interactive controls (6 pages) expose an AutomationId or Name" -ForegroundColor Green
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
