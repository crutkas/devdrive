<#
    ui-tests.ps1 — WinApp UI tests for DevDriveManager (NavigationView shell)

    Follows the winui-ui-testing skill: one batch script, single run, structured results.
    Drives the *built, running* app via `winapp ui` (UI Automation).

    The redesign splits the old single endless-scroll page into a NavigationView shell of three rooms
    plus Settings — Home; Space (one page: the map, with a clean-up panel beside it); Dev Drive
    (tabs: Overview, Package caches, with the sub-pages Create Dev Drive, Compare speed and Drives
    and filters reached from its Overview links under a "Dev Drive > ..." breadcrumb) — all backed
    by ONE shared MainPageViewModel (App.Shared), loaded once. This suite:
      (1) asserts the shell + every nav item renders, and that the retired rail rooms are gone;
      (2) navigates to each page (rail room, shell tab or Dev Drive link) and asserts a page anchor;
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

      # ...and additionally run the Space clean-up scan assertions, which are minutes of real disk I/O:
      .\ui-tests.ps1 -AppPid <PID> -IncludeSlowScans

    Exit code 0 = all passed, 1 = one or more failures, 2 = safe mutation mode is absent.
    Results are also written to test-results.json next to this script.
#>
param(
    [Parameter(Mandatory)][int]$AppPid,

    # A real Space scan maps a whole volume and walks its source trees for clean-up, and takes over a
    # minute, so the assertions that need one are opt-in rather than paid for on every structural run.
    # They are the only place the clean-up panel's post-scan selection state can be observed at all:
    # ReclaimViewModel cannot be link-compiled into a unit test, because StatusEmphasis lives in a
    # control's code-behind and would drag Microsoft.UI.Xaml in with it.
    [switch]$IncludeSlowScans)
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

# Read a raw UIA property (ControlType, IsKeyboardFocusable, ...). Note that LocalizedControlType is
# NOT among the properties this tool surfaces, so an assertion about what a screen reader *says* has to
# be made against what the control *does* instead — which is the stronger claim anyway.
function Get-UiaProperty([string]$id, [string]$property) {
    $json = winapp ui get-property $id -a $AppPid --json 2>$null | ConvertFrom-Json
    $global:LASTEXITCODE = 0
    return [string]$json.properties.$property
}

# Read a control's effective value (ComboBox selected item, TextBox text, etc.).
function Get-Value([string]$id) {
    $json = winapp ui get-value $id -a $AppPid --json 2>$null | ConvertFrom-Json
    return [string]$json.text
}

# Presence PREDICATE built on the tool's own resolver. `winapp ui inspect <id>` silently falls back to
# a whole-tree dump when the id is absent, so substring-matching its output is not a presence check.
#
# This deliberately swallows the exit code, because callers ask it questions whose answer is legitimately
# "no" ("is there a chart *or* an empty state?"). That makes it a predicate, never an assertion — its
# return value is the only signal it produces. Use Assert-Present when absence should fail the test.
function Test-Present([string]$id, [int]$timeoutMs = 1500) {
    winapp ui wait-for $id -a $AppPid -t $timeoutMs 2>&1 | Out-Null
    $found = ($LASTEXITCODE -eq 0)
    $global:LASTEXITCODE = 0     # a legitimate "absent" must not fail the enclosing Test-UI
    return $found
}

# Presence ASSERTION over a set of ids: throws naming the first one missing.
#
# This exists because passing an array to Test-Present's [string]$id silently space-joined it into one
# nonsense id ("CreateFormHead AfterCreateHead ..."), which of course was never found — and since
# Test-Present resets $LASTEXITCODE and its result was discarded, four tests passed while asserting
# nothing at all. A predicate whose answer is thrown away is not a test.
function Assert-Present([string[]]$ids, [int]$timeoutMs = 3000) {
    foreach ($id in $ids) {
        if (-not (Test-Present $id $timeoutMs)) { throw "'$id' is not present." }
    }
}

# Property ASSERTION: a bare `wait-for -p` only fails a test when it is the block's LAST native call,
# and a later Test-Present resets the exit code — so mid-block property checks must throw themselves.
function Assert-Value([string]$id, [string]$property, [string]$value, [int]$timeoutMs = 3000) {
    winapp ui wait-for $id -a $AppPid -p $property --value $value -t $timeoutMs 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "'$id' $property never read '$value'." }
}

# How many rows a list is actually showing.
#
# Counted as ListItems from the control view rather than by AutomationId, because a ListView without a
# ContainerContentChanging hook never puts its per-row ids into UIA. Grepping for row ids there returns
# nothing whether the table is full or empty — a test written that way reports PASS for exactly the
# state it was meant to catch.
#
# `--depth 1` keeps this counting containers rather than the labels inside them.
function Get-RowCount([string]$listId) {
    $text = winapp ui inspect $listId --depth 1 -a $AppPid 2>$null | Out-String
    $global:LASTEXITCODE = 0
    return ([regex]::Matches($text, 'ListItem')).Count
}

# Every AutomationId on screen matching a pattern, polled until at least one appears.
#
# Which ecosystems are installed, and which volume each cache sits on, are properties of THIS
# workstation. Naming one ("Cargo", "npm", "uv") turns "the developer installed something" into a red
# test — this suite has already lost a day to exactly that. Discover the set, then assert the RULE that
# governs it, which holds on any machine.
#
# --interactive, NOT --json: at the root, --json reports slugified selectors (btn-movecache-9f2c) and
# never the raw AutomationId, so a --json grep for an id matches nothing whether the row is there or not.
function Find-Ids([string]$pattern, [int]$timeoutMs = 4000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ($true) {
        $tree = winapp ui inspect -a $AppPid --interactive 2>$null | Out-String
        $global:LASTEXITCODE = 0
        $hits = @([regex]::Matches($tree, $pattern) | ForEach-Object { $_.Value } | Sort-Object -Unique)
        if ($hits.Count -gt 0 -or (Get-Date) -ge $deadline) { return $hits }
        Start-Sleep -Milliseconds 200
    }
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

# Space is one page with no tab bar; what can be cleaned up is the panel beside its map.
function Goto-SpaceMap  { Goto "NavSpace" "SpaceFolderTree" }
function Goto-Reclaim   { Goto "NavSpace" "SpaceCleanupPanel" }
# Dev Drive is one page too; its package caches are the "Recommended" panel beside it.
function Goto-DevDrive  { Goto "NavDrives" "DevDriveScrollViewer" }
function Goto-Caches    { Goto "NavDrives" "DevDriveCachePanel" }

# Dev Drive's sub-pages are reached from links on its page, never from the rail.
function Goto-DevDriveLink([string]$linkId, [string]$anchorId) {
    Goto-DevDrive
    winapp ui invoke $linkId -a $AppPid 2>$null | Out-Null
    Start-Sleep -Milliseconds 500
    winapp ui wait-for $anchorId -a $AppPid -t 5000 | Out-Null
}

# Create is the empty state's button on a PC without a Dev Drive, and "Create another" once one exists.
function Get-CreateTrigger {
    if (Test-Present "CreateDevDriveButton") { return "CreateDevDriveButton" }
    return "DevDriveCreateAnother"
}
function Goto-Create {
    Goto-DevDrive
    winapp ui invoke (Get-CreateTrigger) -a $AppPid 2>$null | Out-Null
    Start-Sleep -Milliseconds 500
    winapp ui wait-for "CreateFormHead" -a $AppPid -t 5000 | Out-Null
}

# Breadcrumb items are generated from strings and carry no AutomationId: find one by Name under
# PageBreadcrumb and invoke its slug straight away (slugs are only valid for the inspect that made them).
function Invoke-Crumb([string]$name) {
    $obj = winapp ui inspect "PageBreadcrumb" -a $AppPid --json --depth 6 2>$null | Out-String | ConvertFrom-Json
    $queue = [System.Collections.Generic.Queue[object]]::new()
    foreach ($w in $obj.windows) { foreach ($e in $w.elements) { $queue.Enqueue($e) } }
    $textMatch = $null
    while ($queue.Count -gt 0) {
        $n = $queue.Dequeue()
        if ($n.name -eq $name) {
            if ($n.className -like '*BreadcrumbBarItem') { $textMatch = $n; break }
            if (-not $textMatch) { $textMatch = $n }
        }
        foreach ($c in $n.children) { $queue.Enqueue($c) }
    }
    if (-not $textMatch) { throw "No '$name' crumb in PageBreadcrumb." }
    # invoke falls back to the nearest invokable ancestor when the match is the crumb's text.
    winapp ui invoke $textMatch.selector -a $AppPid 2>$null | Out-Null
}

# Select a row and wait until a companion element reflects the selection.
#
# This goes through UIA's SelectionItemPattern rather than `winapp ui click`, which simulates a
# mouse: injected input is at the mercy of whatever is on screen at that instant, and a locked-down
# desktop refuses it outright with "SendInput failed" before the app is ever involved. Retry a
# bounded number of times anyway, since the room may still be laying out.
function Select-Until([string]$target, [string]$probeId, [string]$expected, [int]$tries = 4) {
    for ($i = 1; $i -le $tries; $i++) {
        winapp ui invoke $target -a $AppPid 2>$null | Out-Null
        Start-Sleep -Milliseconds (300 * $i)
        if ((Get-Name $probeId) -eq $expected) { return $true }
    }
    return $false
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
Test-UI "Nav: Home present"                 { winapp ui wait-for "NavDashboard" -a $AppPid -p Name --value "Home"      -t 4000 }
Test-UI "Nav: Space present"                { winapp ui wait-for "NavSpace"     -a $AppPid -p Name --value "Space"     -t 4000 }
Test-UI "Nav: Dev Drive present"            { winapp ui wait-for "NavDrives"    -a $AppPid -p Name --value "Dev Drive" -t 4000 }
Test-UI "Nav: Settings present"             { winapp ui wait-for "SettingsItem" -a $AppPid -t 4000 }
# Clean up is a panel on Space, Package caches a Dev Drive tab, Benchmarks and Create Dev Drive
# are Dev Drive sub-pages. None of them is a rail room any more.
Test-UI "Nav: retired rooms are not on the rail" {
    foreach ($id in @("NavReclaim", "NavPackageCaches", "NavBenchmarks", "NavCreate")) {
        if (Test-Present $id 1000) { throw "'$id' should no longer be a rail room." }
    }
}

# ─────────────────────────────────────────────────────────────────────────────
#  (2) Overview — the landing room. Two questions only: how do my drives look (used, reclaimable,
#      free) and what could this PC gain (a Dev Drive, caches moved onto it, space to get back).
#      Everything here is derived from the other rooms' data, so the assertions are about the room's
#      own furniture rather than any particular finding — which findings appear depends entirely on
#      the test machine.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Home"                   { Goto "NavDashboard" "OverviewDrives" }
Test-UI "Home: the room's own title reads Home" {
    winapp ui wait-for "PageTitle" -a $AppPid -p Name --value "Home" -t 3000
}
Test-UI "Overview: no display-only volume strip" { winapp ui wait-for "OverviewVolumeStrip" -a $AppPid --gone -t 3000 }
Test-UI "Overview: scan action"              { winapp ui wait-for "ScanBarScanButton"     -a $AppPid -t 3000 }
Test-UI "Overview: no last-scan caption"     { winapp ui wait-for "LastScanChip"          -a $AppPid --gone -t 3000 }
Test-UI "Overview: opportunities header"     { winapp ui wait-for "OverviewOpportunities" -a $AppPid -t 3000 }
Test-UI "Overview: quick actions"            { winapp ui wait-for "OverviewQuickActions"  -a $AppPid -t 3000 }
Test-UI "Overview: quick actions lead to Space, Clean up and Package caches" {
    Assert-Present @("OverviewQuick_space", "OverviewQuick_reclaim", "OverviewQuick_caches")
    if (Test-Present "OverviewQuick_benchmarks" 500) { throw "Compare speed is no longer a quick action." }
}
Test-UI "Overview: 'See what takes space' opens the Space map" {
    winapp ui invoke "OverviewQuick_space" -a $AppPid 2>$null | Out-Null
    winapp ui wait-for "SpaceFolderTree" -a $AppPid -t 5000 | Out-Null
    Assert-Value "NavSpace" "IsSelected" "True"
    Goto "NavDashboard" "OverviewDrives"
}
Test-UI "Overview: cancel is offered only while scanning" {
    if (Test-Present "OverviewCancelScan") { throw "a cancel button is showing while nothing is scanning" }
}

# Every machine has at least its system drive, so a card for it is a hard requirement. The card's
# name must carry free space, because that is the one figure the room exists to show.
Test-UI "Overview: a card per drive" {
    $card = Get-Selectors "OverviewScrollViewer" | Where-Object { $_ -clike 'OverviewDrive_*' } | Select-Object -First 1
    if (-not $card) { throw "no drive cards rendered" }
    $name = Get-Name $card
    if ($name -notmatch 'free') { throw "$card does not say how much is free: '$name'" }
}

# The suggestions list and its empty state are mutually exclusive, and which one shows depends on what
# the machine actually has to offer — so assert that the room committed to one of them.
Test-UI "Overview: opportunities or empty state" {
    $row = Get-Selectors "OverviewScrollViewer" | Where-Object { $_ -clike 'Signal_*' } | Select-Object -First 1
    if (-not $row -and -not (Test-Present "OverviewOpportunitiesEmpty")) {
        throw "neither an opportunity nor the empty state rendered"
    }
}

Test-UI "Overview: an opportunity's action opens its room" {
    $button = Get-Selectors "OverviewScrollViewer" | Where-Object { $_ -clike 'SignalRoom_*' } | Select-Object -First 1
    if (-not $button) { winapp ui wait-for "OverviewDrives" -a $AppPid -t 3000 | Out-Null; return }
    winapp ui invoke $button -a $AppPid 2>$null | Out-Null
    Start-Sleep -Milliseconds 700
    # Whichever room it landed in, Overview is no longer the one on screen.
    if (Test-Present "OverviewDrives") { throw "$button did not leave the Overview room" }
    Goto "NavDashboard" "OverviewDrives"
}
winapp ui screenshot -a $AppPid -o "screenshots\01-overview.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  (2b) Clean up — the panel beside Space's map, the part the Storage Manager reframe is built around.
#       Scanning is deliberately NOT started on navigation (a real scan is minutes of I/O), so these
#       assert the resting state: the panel renders, says how to fill it, and Scan is offered.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Space's clean-up panel" { Goto-Reclaim }
Test-UI "Clean up: a panel on Space, not a tab or a room" {
    Assert-Value "NavSpace" "IsSelected" "True"
    Assert-Value "PageTitle" "Name" "Space"
    foreach ($id in @("RoomTabs", "Tab_SpaceMap", "Tab_SpaceCleanUp", "ReclaimCategoryList")) {
        if (Test-Present $id 1000) { throw "'$id' should not be present on Space." }
    }
}
Test-UI "Clean up: Scan present"             { winapp ui wait-for "ScanBarScanButton"   -a $AppPid -t 3000 }
Test-UI "Clean up: does not scan on entry" {
    $s = Get-Name "SpaceCleanupEmpty"
    if ($s -notmatch 'Scan a drive') { throw "expected an unscanned resting state, got '$s'" }
}
# Cancel is bound to Visibility, and a stop button offered when nothing is running is a lie about what
# the app is doing. Collapsed controls have no UIA peer at all (verified), so absence here is a real
# assertion: binding it to something always-true turns this red.
Test-UI "Clean up: stop is offered only while something is running" {
    if (Test-Present "SpaceCancelScan")         { throw "Cancel is offered while no scan is running" }
    if (Test-Present "SpaceCancelProgressRing") { throw "a progress ring is spinning while nothing is running" }
}
# Nothing is preselected. This is the panel's most important safety property and the one thing about it
# that a user could get wrong in a single click: an earlier build ran SelectSafe() after every scan, so
# a page opened to "let me look" arrived with 404 GB ticked and a live delete button. The resting state
# is the cheap half of the assertion — the scanned half is at the end of the suite, because a real scan
# is ~70 s of I/O and belongs nowhere near the fast structural passes.
Test-UI "Clean up: nothing is selected at rest" {
    if (Test-Present "SpaceCleanupButton") {
        $label = Get-Name "SpaceCleanupButton"
        if ($label -ne "Clean up") { throw "the panel opened with something already ticked: '$label'" }
        if ((Get-UiaProperty "SpaceCleanupButton" "IsEnabled") -eq "True") {
            throw "the delete button is live with nothing selected"
        }
    }
}
winapp ui screenshot -a $AppPid -o "screenshots\01b-reclaim.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  (2c) Space — the folder explorer. Like the clean-up panel it does not scan on entry (a cold full-volume scan
#       is minutes of I/O), so these assert the room's furniture and, crucially, the unmeasured
#       contract: a folder nobody has measured must read "—", never "0 bytes". That distinction is
#       the whole reason the unmeasured plumbing exists, and until now no UI test covered this room
#       at all — it appeared in the suite only as a page the accessibility audit walked past.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Space"                  { Goto-SpaceMap }
Test-UI "Space: the shell owns the title, and there are no tabs" {
    Assert-Value "PageTitle" "Name" "Space"
    if (Test-Present "RoomTabs" 1000) { throw "Space is one page; the tab bar should be hidden." }
    winapp ui wait-for "SpaceCleanupPanel" -a $AppPid -t 3000
}
Test-UI "Space: volume ComboBox"             { winapp ui wait-for "SpaceVolumeStrip"     -a $AppPid -t 3000 }
Test-UI "Space: folder tree"                 { winapp ui wait-for "SpaceFolderTree"      -a $AppPid -t 3000 }
Test-UI "Space: treemap"                     { winapp ui wait-for "SpaceTreemap"         -a $AppPid -t 3000 }
Test-UI "Space: treemap scope label removed" { winapp ui wait-for "SpaceBreadcrumb" -a $AppPid --gone -t 1000 }
Test-UI "Space: duplicate table, selector and tree header removed" {
    foreach ($id in @("SpaceInspectorTitle", "SpaceInspectorPath", "SpaceItemsList",
                      "SpaceViewMode", "SpaceModeFolders", "SpaceModeLargestFiles", "SpaceRootName")) {
        if (Test-Present $id) { throw "'$id' should not be present on Space." }
    }
}
Test-UI "Space: bottom bar hidden while idle" {
    if (Test-Present "SpaceStatusBar") { throw "Idle scan bar must be hidden" }
}
Test-UI "Space: rescan is offered, not running" {
    Assert-Present @("ScanBarScanButton", "SpaceFolderTree")
    if (Test-Present "SpaceCancelScan") { throw "Cancel is offered while no scan is running" }
    if (Test-Present "SpaceScanProgress") { throw "a progress bar is shown while no scan is running" }
}
# The ComboBox owns the selected scope; the adjacent action keeps its fixed Scan label.
Test-UI "Space: picker owns scope and scan button has a fixed label" {
    $label = Get-Name "ScanBarScanButton"
    if ($label -ne 'Scan') {
        throw "expected fixed Scan label, got '$label'"
    }
    if ((Get-UiaProperty "SpaceVolumeStrip" "ClassName") -ne "ComboBox") {
        throw "Volume selection must use a native ComboBox"
    }
}
# The unmeasured contract, asserted where a user would actually read it. An unscanned root reports its
# size as an em dash; "0 bytes" would be a claim we measured it and found nothing there.
Test-UI "Space: an unmeasured size reads as a dash, not zero" {
    $detail = @(winapp ui inspect "SpaceFolderTree" -a $AppPid --depth 3 2>$null) -join "`n"
    if ($detail -match '\b0 bytes\b') { throw "unmeasured coverage reported '0 bytes' instead of '—': '$detail'" }
}
winapp ui screenshot -a $AppPid -o "screenshots\01c-space.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  (2d) The scan grammar, across rooms.
#
#  Overview and Space each used to invent their own scan affordance: different labels, different
#  enablement rules, and — on Space alone — a volume chip that silently started a full volume scan
#  when clicked. These assert the common interaction contract:
#
#    · The whole-machine action names its scope; Space's ComboBox names its scope beside Scan.
#    · Selection never starts work; only the button does.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Scan grammar: the whole-machine scan names its scope" {
    Goto "NavDashboard" "OverviewDrives"
    $overview = Get-Name "ScanBarScanButton"
    if ($overview -notmatch '^Scan this PC$') {
        throw "expected the whole-machine label, got '$overview'"
    }
}
# The volume picker is the scope control, so it must be reachable from the keyboard.
Test-UI "Scan grammar: the volume picker is a tab stop" {
    Goto-SpaceMap
    if ((Get-UiaProperty "SpaceVolumeStrip" "IsKeyboardFocusable") -ne "True") {
        throw "the scope picker cannot be reached from the keyboard"
    }
}
# Picking a scope is not the same request as "scan it". Space used to conflate them, so a click meant
# to change what you were looking at started minutes of I/O with no confirmation.
Test-UI "Scan grammar: picking a scope does not start a scan" {
    Goto-SpaceMap
    winapp ui invoke "SpaceVolumeStrip" -a $AppPid 2>$null | Out-Null
    winapp ui send-keys "down enter" --target "SpaceVolumeStrip" -a $AppPid 2>$null | Out-Null
    Start-Sleep -Milliseconds 900
    if (Test-Present "SpaceCancelScan") { throw "choosing a volume started a scan" }
    if ((Get-Name "ScanBarScanButton") -ne "Scan") { throw "the scan label changed with the chosen scope" }
}

# ─────────────────────────────────────────────────────────────────────────────
#  (3) Package caches — the "Recommended for your Dev Drive" panel beside the Dev Drive page. It
#      used to be a second tab; old "caches" links now open the Dev Drive page.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Dev Drive's cache panel" { Goto-Caches }
Test-UI "Caches: a panel on Dev Drive, not a tab" {
    Assert-Value "NavDrives" "IsSelected" "True"
    Assert-Value "PageTitle" "Name" "Dev Drive"
    foreach ($id in @("RoomTabs", "Tab_DevDriveOverview", "Tab_PackageCaches", "PackageCachesScrollViewer")) {
        if (Test-Present $id 1000) { throw "'$id' should not be present on Dev Drive." }
    }
}
# Which ecosystems are installed, and which volume each cache sits on, are facts about THIS PC — so
# both assertions below discover the set and check the rule. They used to name Cargo and npm, which is
# the same scar that took MapPathInput_uv red when uv was installed on this workstation.
Test-UI "Caches: every listed cache names the volume it is on" {
    $cards = Find-Ids 'PackageCacheCard_[A-Za-z0-9]+'
    if ($cards.Count -eq 0) {
        if (Test-Present "DevDriveCacheEmpty" 500) { return }
        throw "No cache rows and no empty state"
    }
    foreach ($c in $cards) {
        if ((Get-Name $c) -notmatch ',\s*On [A-Za-z]:$') { throw "$c does not name the volume it is on" }
    }
}
# The grouping rule stated AS a rule: CanMove = hasDevDrive && detected && !onDevDrive, so a cache
# already on the Dev Drive must offer no tick box and every cache elsewhere must offer one. The Dev
# Drive letter is read off the panel's summary once measuring settles, so this holds on any machine.
Test-UI "Caches: only caches off the Dev Drive can be ticked to move" {
    $deadline = (Get-Date).AddSeconds(30)
    do {
        $detail = Get-Name "DevDriveCacheDetail"
        if ($detail -notmatch '^(Looking|Measuring)') { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    $moves = @(Find-Ids 'SelectCache_[A-Za-z0-9]+' 1500 | ForEach-Object { $_ -replace '^SelectCache_', '' })
    if ($detail -match 'Create a Dev Drive') {
        if ($moves.Count -gt 0) { throw "a move is offered without a Dev Drive" }
        return
    }
    if ($detail -notmatch '(?:to|on) ([A-Za-z]):\.$') { throw "The panel summary does not name the Dev Drive: '$detail'" }
    $dev = $Matches[1]
    foreach ($card in (Find-Ids 'PackageCacheCard_[A-Za-z0-9]+')) {
        $eco    = $card -replace '^PackageCacheCard_', ''
        $onDev  = (Get-Name $card) -match ",\s*On $dev`:"
        $offers = $moves -contains $eco
        if ($onDev -and $offers)          { throw "$eco is already on ${dev}: yet still offers a move" }
        if (-not $onDev -and -not $offers) { throw "$eco is not on ${dev}: yet offers no move" }
    }
}
# The old tab's table furniture, its hidden "Not installed" view, inspector and footer are all gone.
Test-UI "Caches: no table, tab strip, inspector or footer" {
    foreach ($id in @("CachesTableTitle", "CachesStatus", "CacheTabStrip", "CacheTab_Detected",
                      "CacheTab_NotInstalled", "CachesInspectorName", "CachesStatusBar", "DevDriveCachesLink")) {
        if (Test-Present $id 500) { throw "'$id' should be gone." }
    }
}
Test-UI "Caches: cache-move guide link"      { winapp ui wait-for "LearnWhatThisDoes" -a $AppPid -t 3000 }
# Move is offered only when something can move, so the tests below are skipped when nothing can.
Test-UI "Caches: movable caches start ticked, with one Move button and no per-row Move" {
    $boxes = @(Find-Ids 'SelectCache_[A-Za-z0-9]+' 1500)
    if ($boxes.Count -eq 0) { return }
    foreach ($b in $boxes) { Assert-Value $b "ToggleState" "On" }
    if (@(Find-Ids 'MoveCache_[A-Za-z0-9]+' 500).Count -gt 0) { throw "Rows should no longer have their own Move button." }
    Assert-Present @("MoveAllButton")
}
Test-UI "Caches: Move opens a dialog; Cancel keeps the ticked caches" {
    if (-not (Test-Present "MoveAllButton" 3000)) { return }
    $before = @(Find-Ids 'SelectCache_[A-Za-z0-9]+' 1500).Count
    winapp ui invoke "MoveAllButton" -a $AppPid
    try {
        winapp ui wait-for "MoveAllConfirmDialog" -a $AppPid -t 3000
    }
    finally {
        if (Test-Present "MoveAllConfirmDialog") {
            winapp ui invoke "CloseButton" -a $AppPid 2>$null | Out-Null
        }
    }
    winapp ui wait-for "MoveAllButton" -a $AppPid -t 3000
    if (@(Find-Ids 'SelectCache_[A-Za-z0-9]+' 1500).Count -ne $before) {
        throw "Cancelling Move changed the caches that can move"
    }
}
winapp ui screenshot -a $AppPid -o "screenshots\02-caches.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  (3b) Dev Drive — one page, no tabs. Its furniture depends on whether this PC already has a Dev
#       Drive: an empty state holding the one Create button, or the drive cards, the "Set up"
#       section and "Create another". Either way its links route to the room's sub-pages.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Dev Drive"              { Goto-DevDrive }
Test-UI "Dev Drive: the shell owns the title, and there are no tabs" {
    Assert-Value "NavDrives" "IsSelected" "True"
    Assert-Value "PageTitle" "Name" "Dev Drive"
    if (Test-Present "RoomTabs" 1000) { throw "Dev Drive is one page; the tab bar should be hidden." }
}
Test-UI "Dev Drive: more heading"            { Assert-Present @("DevDriveMoreHead") }
# DevDriveStorageSettingsLink opens ms-settings:storage — it is checked for presence, never invoked.
Test-UI "Dev Drive: links to speed, volumes and Storage settings" {
    Assert-Present @("DevDriveCompareSpeedLink", "DevDriveVolumesLink", "DevDriveStorageSettingsLink")
}
# Whether this PC has a Dev Drive is a fact about the machine, so assert the rule rather than a side:
# exactly one of cards / empty state, and exactly one way to Create.
Test-UI "Dev Drive: drive cards or the empty state, never both" {
    $cards = Test-Present "DevDriveCards" 1500
    $empty = Test-Present "DevDriveEmptyState" 500
    if ($cards -eq $empty) { throw "Expected exactly one of DevDriveCards / DevDriveEmptyState (cards=$cards, empty=$empty)." }
    if ($empty) {
        Assert-Present @("CreateDevDriveButton")
        foreach ($id in @("DevDriveCreateAnother", "DevDrivePerformanceMode", "DevDriveSetupHead")) {
            if (Test-Present $id 500) { throw "'$id' needs an existing Dev Drive." }
        }
    } else {
        if (Test-Present "CreateDevDriveButton" 500) { throw "CreateDevDriveButton belongs to the empty state only." }
        Assert-Present @("DevDriveSetupHead", "DevDriveCreateAnother", "DevDrivePerformanceMode")
    }
}
winapp ui screenshot -a $AppPid -o "screenshots\04a-devdrive.png" 2>$null | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
#  (4) Benchmarks — "Compare speed", a sub-page of Dev Drive reached from its Overview tab.
#      Strip / tabbed table / measured-runs chart / inspector. Two tabs: how fast it is today, and
#      what would change that.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Dev Drive > Compare speed" { Goto-DevDriveLink "DevDriveCompareSpeedLink" "WorkloadsList" }
Test-UI "Benchmarks: Dev Drive stays selected and the header is a breadcrumb" {
    Assert-Value "NavDrives" "IsSelected" "True"
    Assert-Present @("PageBreadcrumb")
    if (Test-Present "PageTitle" 500) { throw "The plain page title should give way to the breadcrumb." }
    if (Test-Present "RoomTabs" 500)  { throw "Room tabs should be hidden on a sub-page." }
}
Test-UI "Benchmarks: Run all present"        { winapp ui wait-for "RunAllTestsButton" -a $AppPid -t 4000 }
Test-UI "Benchmarks: tab strip present"      { winapp ui wait-for "BenchTab_Workloads" -a $AppPid -t 3000 }
Test-UI "Benchmarks: git-clone workload row" { winapp ui wait-for "BenchRow_git-clone" -a $AppPid -t 4000 }
Test-UI "Benchmarks: git-clone Run button"   { winapp ui wait-for "RunRow_git-clone"   -a $AppPid -t 4000 }
Test-UI "Benchmarks: measured-runs card"     { winapp ui wait-for "MeasuredRunsList"   -a $AppPid -t 4000 }
Test-UI "Benchmarks: git-clone row in measured runs" { winapp ui wait-for "BenchRuns_git-clone" -a $AppPid -t 4000 }

# Nothing has been run, so every cell must say so rather than sit blank — an empty cell under a
# column header reads as data we failed to load, not as a measurement that has not happened.
Test-UI "Benchmarks: unrun row shows an em dash, not a blank" {
    $n = Get-Name "BenchSystem_git-clone"
    if ($n -ne [char]0x2014) { throw "expected an em dash in the unrun system cell but got: '$n'" }
}
Test-UI "Benchmarks: unrun difference reads 'Not measured'" {
    $n = Get-Name "BenchDelta_git-clone"
    if ($n -ne 'Not measured') { throw "expected 'Not measured' in the difference pill but got: '$n'" }
}
Test-UI "Benchmarks: head chip counts what is measured" {
    $n = Get-Name "BenchmarksStatus"
    if ($n -notmatch 'measured') { throw "expected the head chip to count measurements but got: '$n'" }
}

# The inspector explains the selected workload. The room selects the first row on load, so it must
# be answering a real question before anything is clicked.
Test-UI "Benchmarks: inspector names a workload" {
    $n = Get-Name "BenchInspectorTitle"
    if ([string]::IsNullOrWhiteSpace($n) -or $n -eq 'Select a workload') {
        throw "expected the inspector to open on the first workload but got: '$n'"
    }
}
Test-UI "Benchmarks: inspector shows the command"  { winapp ui wait-for "BenchInspectorCommand" -a $AppPid -t 3000 }
Test-UI "Benchmarks: honesty line is present"      { winapp ui wait-for "SuiteHonestyLine"      -a $AppPid -t 3000 }
Test-UI "Benchmarks: raw-throughput note present"  { winapp ui wait-for "DiskSpeedAbsenceNote"  -a $AppPid -t 3000 }
Test-UI "Benchmarks: status bar present"           { winapp ui wait-for "BenchmarksStatusBar"   -a $AppPid -t 3000 }

# The Suggestions tab is the only place the suite's Upside* copy renders. Switching to it must swap
# the table, not merely highlight a tab.
Test-UI "Benchmarks: switch to Suggestions" {
    winapp ui invoke "BenchTab_Suggestions" -a $AppPid 2>$null | Out-Null
    Start-Sleep -Milliseconds 400
    winapp ui wait-for "SuggestionsList" -a $AppPid -t 4000
}
Test-UI "Benchmarks: source lever present"   { winapp ui wait-for "Suggestion_Source" -a $AppPid -t 3000 }
Test-UI "Benchmarks: caches lever present"   { winapp ui wait-for "Suggestion_Caches" -a $AppPid -t 3000 }
Test-UI "Benchmarks: a lever routes somewhere" {
    $n = Get-Name "Suggestion_Caches_Action"
    if ([string]::IsNullOrWhiteSpace($n)) { throw "expected the caches lever to carry a room button" }
}
winapp ui screenshot -a $AppPid -o "screenshots\03-benchmarks.png" 2>$null | Out-Null
Test-UI "Benchmarks: back to Workloads" {
    winapp ui invoke "BenchTab_Workloads" -a $AppPid 2>$null | Out-Null
    Start-Sleep -Milliseconds 400
    winapp ui wait-for "WorkloadsList" -a $AppPid -t 4000
}

# ─────────────────────────────────────────────────────────────────────────────
#  (5) Drives and filters — a Dev Drive sub-page: volumes / filter drivers tabs over one table.
#      G: is the real ReFS Dev Drive. Creating one is no longer offered here (see section 6).
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Dev Drive > Drives and filters" { Goto-DevDriveLink "DevDriveVolumesLink" "VolumesList" }
Test-UI "Drives: a breadcrumb under Dev Drive, with no Create button" {
    Assert-Value "NavDrives" "IsSelected" "True"
    Assert-Present @("PageBreadcrumb")
    if (Test-Present "CreateDevDriveButton" 500) { throw "Create belongs to the Dev Drive overview, not Drives and filters." }
}
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
#  (6) Create Dev Drive — a sub-page of Dev Drive, not a rail room. The form is a field list, not a
#      wizard, and one confirmation verifies and executes through the safe in-memory seam.
# ─────────────────────────────────────────────────────────────────────────────
Test-UI "Navigate to Create Dev Drive"       { Goto-Create }
Test-UI "Create: Dev Drive stays selected and the header is a breadcrumb" {
    Assert-Value "NavDrives" "IsSelected" "True"
    Assert-Present @("PageBreadcrumb")
    if (Test-Present "PageTitle" 500) { throw "The plain page title should give way to the breadcrumb." }
}
Test-UI "Create: room grammar is present" {
    Assert-Present @("CreateFormHead", "AfterCreateHead")
}
Test-UI "Create: inline plan removed" {
    foreach ($id in @("PlanStepsHead", "GuardrailsInfoBar")) {
        if (Test-Present $id) { throw "'$id' should not be present on Create." }
    }
}
Test-UI "Create: size row carries a slider and its ceiling" {
    Assert-Present @("SizeSlider", "SizeNumberBox", "SizeMaximumTick")
    if ((Get-Name "SizeMaximumTick") -notmatch '^[0-9,]+ GB \u2014 ') { throw "Maximum tick does not name its ceiling." }
}
Test-UI "Create: format is stated, not chosen" {
    if ((Get-Name "FormatPill") -notmatch 'ReFS') { throw "Format pill does not say ReFS." }
}
Test-UI "Create: reclaim advice and footer removed" {
    foreach ($id in @("ReclaimTieInHead", "ReclaimTieIn", "FindSpaceFirstButton", "CreateStatusBar")) {
        if (Test-Present $id) { throw "'$id' should not be present on Create." }
    }
}
winapp ui screenshot -a $AppPid -o "screenshots\05-create.png" 2>$null | Out-Null

Test-UI "Create: method is two options, resize is the default" {
    Assert-Present @("MethodResizeOption", "MethodVhdxOption")
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
    Assert-Present @("CompletionTitle", "MovePackageCachesButton", "RunSpeedTestButton")
}
winapp ui screenshot -a $AppPid -o "screenshots\08-create-complete.png" 2>$null | Out-Null

# The breadcrumb's first crumb is the room: it leaves the sub-page for the Dev Drive overview.
Test-UI "Create: the first crumb returns to Dev Drive" {
    Invoke-Crumb "Dev Drive"
    winapp ui wait-for "DevDriveScrollViewer" -a $AppPid -t 5000 2>$null | Out-Null
    Assert-Value "PageTitle" "Name" "Dev Drive"
    if (Test-Present "PageBreadcrumb" 500) { throw "The breadcrumb should give way to the room title." }
}

# ─────────────────────────────────────────────────────────────────────────────
#  (7) Settings — native cards and expanders, every preference reaches a real decision.
#      The suite changes each preference, asserts the control reflects it, then puts it back, so a
#      run leaves the machine and the app exactly as it found them.
# ─────────────────────────────────────────────────────────────────────────────
function Expand-SettingsGroup([string]$id) {
    $tree = winapp ui inspect $id -a $AppPid --json --depth 2 | Out-String | ConvertFrom-Json
    $header = $tree.windows[0].elements[0].children |
        Where-Object className -eq 'Microsoft.UI.Xaml.Controls.Expander' | Select-Object -First 1
    if (-not $header) { throw "No native Expander peer for $id." }
    if ($header.expandState -ne 'expanded') {
        winapp ui invoke $header.selector -a $AppPid | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not expand $id." }
    }
}

Test-UI "Navigate to Settings" {
    winapp ui invoke "SettingsItem" -a $AppPid 2>$null | Out-Null
    Start-Sleep -Milliseconds 500
    winapp ui wait-for "ThemeSelector" -a $AppPid -t 5000 | Out-Null
}
Test-UI "Settings: machine action shortcuts removed" {
    foreach ($id in @("RescanButton", "SettingsCreateButton", "SettingsManageInStorageButton", "SettingsOpenWindowsSecurityButton")) {
        if (Test-Present $id) { throw "'$id' should not be present in Settings." }
    }
}
Test-UI "Settings: grouped sections"         {
    if (-not (Test-Present "SignalSettingsHead")) { throw "Overview-and-signals card head missing" }
    winapp ui wait-for "MachineSettingsHead" -a $AppPid -t 3000
}
Test-UI "Settings: obsolete content removed" {
    winapp ui wait-for "SettingsStatusBar" -a $AppPid --gone -t 3000
    winapp ui wait-for "WriteSurfaceHead" -a $AppPid --gone -t 3000
}
Test-UI "Settings: expand preference groups" {
    Expand-SettingsGroup CacheSignalsExpander
}
Test-UI "Settings: free-space history removed" {
    foreach ($id in @("HistorySettingsExpander", "RecordHistoryToggle", "RetentionCombo", "ClearHistoryButton")) {
        if (Test-Present $id) { throw "'$id' should not be present in Settings." }
    }
}
Test-UI "Settings: reset present"            { winapp ui wait-for "ResetPreferencesButton" -a $AppPid -t 3000 }
winapp ui screenshot -a $AppPid -o "screenshots\09-settings.png" 2>$null | Out-Null

# Every preference control is reachable. A settings page whose controls do not resolve is the one
# failure mode that looks identical to a working page in a screenshot.
foreach ($pref in @("WatchCachesToggle", "LowFreeCombo", "RollupCombo",
                    "CreationMethodCombo")) {
    Test-UI "Settings: $pref present" { winapp ui wait-for $pref -a $AppPid -t 3000 }
}

# Round-trip one combo of each kind. The threshold reaches AttentionSignalBuilder and the method
# reaches the Create room, so proving the control moves is proving the preference moves.
Select-Combo "LowFreeCombo" "LowFree25"
# Recreate the page to verify the preference reached the store, not only the ComboBox.
Test-UI "Preference: low-free threshold -> 25%, retained across navigation" {
    winapp ui invoke "NavDrives" -a $AppPid | Out-Null
    winapp ui wait-for "DevDriveScrollViewer" -a $AppPid -t 3000 | Out-Null
    winapp ui invoke "SettingsItem" -a $AppPid | Out-Null
    winapp ui wait-for "LowFreeCombo" -a $AppPid --value "25%" -t 3000
    Expand-SettingsGroup CacheSignalsExpander
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
#  (7b) The Space scan's clean-up check — opt-in, because it is minutes of real disk I/O.
#
#  This is the only place the clean-up panel's post-scan state can be observed. The property it exists
#  to hold is a safety one: a scan finds hundreds of gigabytes across seven categories, and the panel
#  must arrive at the end of it with NOTHING ticked. An earlier build ran SelectSafe() automatically, so
#  "let me see what's here" produced 404 GB selected and a live delete button one misread click away.
#
#  It also covers the scan affordance in its running state, which no resting-state test can: Scan
#  steps aside, Cancel takes its place with progress inside it.
# ─────────────────────────────────────────────────────────────────────────────
if ($IncludeSlowScans) {
    Test-UI "Clean up: a Space scan also looks for things to clean up, and reports itself while it does" {
        Goto-Reclaim
        winapp ui invoke "ScanBarScanButton" -a $AppPid 2>$null | Out-Null
        Start-Sleep -Milliseconds 1500

        if (-not (Test-Present "SpaceCancelScan"))         { throw "no way to stop a running scan" }
        if (-not (Test-Present "SpaceCancelProgressRing")) { throw "a running scan shows no progress" }
        # Scan is collapsed while busy; if it is still on screen it must at least be dead.
        if ((Test-Present "ScanBarScanButton" 500) -and
            (Get-UiaProperty "ScanBarScanButton" "IsEnabled") -eq "True") {
            throw "the scan button is still live during its own scan"
        }
    }

    Test-UI "Clean up: the scan finishes" {
        # Worktrees gate the run at ~70 s on a developer machine; allow generous headroom for a cold
        # cache without waiting forever on a genuinely wedged scan. Cancel stays up until both the
        # map's scan and the clean-up check beside it are done.
        $deadline = (Get-Date).AddMinutes(8)
        while ((Test-Present "SpaceCancelScan" 1000) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 5
        }
        if (Test-Present "SpaceCancelScan" 1000) { throw "the scan did not finish within 8 minutes" }
        $found = Get-Name "SpaceCleanupHeadline"
        if ($found -match '^\s*(0 B|—)?\s*$') {
            throw "the scan finished having found nothing at all: '$(Get-Name "SpaceCleanupEmpty")'"
        }
    }

    # THE assertion. A scan is a question, not an instruction.
    Test-UI "Clean up: a finished scan selects nothing" {
        $label = Get-Name "SpaceCleanupButton"
        if ($label -ne "Clean up") {
            throw "the scan pre-selected something for deletion: '$label'"
        }
        if ((Get-UiaProperty "SpaceCleanupButton" "IsEnabled") -eq "True") {
            throw "the delete button is live immediately after a scan, with nothing knowingly chosen"
        }
    }

    # ...and choosing is still one click away, so the default is a choice rather than an obstacle.
    # The panel has no Clear button; the rescan below clears the selection (every scan does).
    Test-UI "Clean up: selecting the safe tier is one click" {
        winapp ui invoke "SpaceCleanupSelectSafe" -a $AppPid 2>$null | Out-Null
        Start-Sleep -Milliseconds 800
        $label = Get-Name "SpaceCleanupButton"
        if ($label -notmatch '^Clean up \S') { throw "Select safe selected nothing: '$label'" }
        if ((Get-UiaProperty "SpaceCleanupButton" "IsEnabled") -ne "True") {
            throw "something is selected but the clean-up button is dead"
        }
    }

    # The groups are copied off the app-lifetime ViewModel when the page loads, so leaving Space and
    # coming back has to rebuild the same list rather than an empty one.
    Test-UI "Clean up: the groups survive leaving the room" {
        $before = @(Find-Ids 'SpaceCleanupGroup_[A-Za-z0-9_-]+' 1500).Count
        if ($before -eq 0) { throw "the scan produced no groups, so the round trip proves nothing" }

        Goto "NavDashboard" "OverviewDrives"
        Goto-Reclaim
        Start-Sleep -Milliseconds 800

        $after = @(Find-Ids 'SpaceCleanupGroup_[A-Za-z0-9_-]+' 1500).Count
        if ($after -ne $before) {
            throw "the panel lost its groups on the way out and back: $before -> $after"
        }
    }
    winapp ui screenshot -a $AppPid -o "screenshots\01b-reclaim-scanned.png" 2>$null | Out-Null

    # A populated shared hierarchy survives navigation even though the visible breadcrumb is gone.
    Test-UI "Space: the hierarchy survives leaving the room and coming back" {
        Goto-SpaceMap
        winapp ui invoke "ScanBarScanButton" -a $AppPid 2>$null | Out-Null

        $deadline = (Get-Date).AddMinutes(2)
        $before = @()
        while ((Get-Date) -lt $deadline -and $before.Count -eq 0) {
            Start-Sleep -Seconds 2
            $before = @(winapp ui inspect "SpaceFolderTree" -a $AppPid --depth 4 2>$null |
                Select-String -Pattern 'FolderTreeItem_')
        }
        if ($before.Count -eq 0) { throw "no tree item appeared, so the round trip proves nothing" }

        if (Test-Present "SpaceCancelScan" 1000) {
            winapp ui invoke "SpaceCancelScan" -a $AppPid 2>$null | Out-Null
            Start-Sleep -Milliseconds 800
        }

        Goto "NavDashboard" "OverviewDrives"
        Goto-SpaceMap
        Start-Sleep -Milliseconds 800

        $after = @(winapp ui inspect "SpaceFolderTree" -a $AppPid --depth 4 2>$null |
            Select-String -Pattern 'FolderTreeItem_')
        if ($after.Count -eq 0) {
            throw "the shared hierarchy did not survive navigation"
        }
    }
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
    @{ nav = "NavDashboard";                                    anchor = "OverviewDrives" },
    @{ nav = "NavSpace";                                        anchor = "SpaceFolderTree" },
    @{ nav = "NavDrives";                                       anchor = "DevDriveScrollViewer" },
    @{ nav = "NavDrives"; then = "DevDriveCompareSpeedLink"; anchor = "WorkloadsList" },
    @{ nav = "NavDrives"; then = "DevDriveVolumesLink";      anchor = "VolumesList" },
    @{ nav = "NavDrives"; then = "create";                   anchor = "CreateFormHead" },
    @{ nav = "SettingsItem";                                    anchor = "ThemeSelector" }
)
$inaccessible = @()
$auditedCount = 0
foreach ($p in $auditPages) {
    winapp ui invoke $p.nav -a $AppPid 2>$null | Out-Null
    if ($p.tab) {
        # Invoking the room that is already selected keeps its current tab, so pick the tab explicitly.
        winapp ui wait-for $p.tab -a $AppPid -t 4000 2>$null | Out-Null
        winapp ui invoke $p.tab -a $AppPid 2>$null | Out-Null
    }
    if ($p.then) {
        # Sub-pages are reached from links on the Dev Drive overview, not from the rail. Create is the
        # empty state's button or "Create another", depending on whether this PC has a Dev Drive.
        winapp ui wait-for "DevDriveScrollViewer" -a $AppPid -t 4000 2>$null | Out-Null
        $link = if ($p.then -eq "create") { Get-CreateTrigger } else { $p.then }
        winapp ui wait-for $link -a $AppPid -t 4000 2>$null | Out-Null
        winapp ui invoke $link -a $AppPid 2>$null | Out-Null
    }
    $label = (@($p.nav, $p.tab, $p.then) | Where-Object { $_ }) -join " > "
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
        $inaccessible += "${label}: $($e.type) (no AutomationId, no Name)"
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
