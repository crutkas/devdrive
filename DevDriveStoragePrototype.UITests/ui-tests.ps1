param(
    [Parameter(Mandatory)]
    [int]$AppPid
)

$ErrorActionPreference = "Continue"
$pass = 0
$fail = 0
$results = @()
$screenshots = @()
$scriptRoot = $PSScriptRoot
$screenshotRoot = Join-Path $scriptRoot "screenshots"
New-Item -ItemType Directory -Force -Path $screenshotRoot | Out-Null

function Test-UI {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Script
    )

    try {
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:pass++
            $script:results += [ordered]@{ name = $Name; status = "PASS" }
        }
        else {
            throw "$output"
        }
    }
    catch {
        $script:fail++
        $script:results += [ordered]@{
            name = $Name
            status = "FAIL"
            detail = "$_"
        }
    }
}

function Save-StateScreenshot {
    param([string]$Name)

    $path = Join-Path $screenshotRoot "$Name.png"
    winapp ui screenshot -a $AppPid -o $path 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $script:screenshots += $path
    }
}

function Select-ComboOption {
    param([string]$ComboId, [string]$OptionName)

    # Scenario switches rebuild thousands of rows, so UI Automation calls can time
    # out while the app is busy. Retry the open, then wait for the popup to settle.
    $popup = $null
    for ($attempt = 1; $attempt -le 4 -and -not $popup; $attempt++) {
        winapp ui invoke $ComboId -a $AppPid 2>$null | Out-Null
        Start-Sleep -Milliseconds (250 * $attempt)
        $windows = winapp ui list-windows -a $AppPid --json 2>$null | ConvertFrom-Json
        $popup = $windows | Where-Object { $_.title -eq "PopupHost" } | Select-Object -First 1
    }
    if (-not $popup) { throw "PopupHost did not appear for $ComboId." }

    $search = winapp ui search $OptionName -w $popup.hwnd --json 2>$null | ConvertFrom-Json
    $selector = @($search.matches | Where-Object { $_.name -eq $OptionName } |
        Select-Object -First 1).selector
    if (-not $selector) { throw "Could not find '$OptionName' in PopupHost." }
    winapp ui invoke $selector -w $popup.hwnd | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not choose '$OptionName'." }
    Start-Sleep -Milliseconds 300
}


# ---------------------------------------------------------------- baseline ---

Test-UI "Workbench appears" {
    winapp ui wait-for "ScopeStatusText" -a $AppPid -t 8000
}
Test-UI "Baseline is 712 GB allocated" {
    winapp ui wait-for "ScopeStatusText" -a $AppPid --value "712 GB" --contains -t 8000
}
Test-UI "Scope reports logical total separately" {
    winapp ui wait-for "ScopeLogicalText" -a $AppPid --value "1.81 TB" --contains -t 4000
}
Test-UI "Core panes are present" {
    winapp ui wait-for "FolderTree" -a $AppPid -t 3000
    if ($LASTEXITCODE -ne 0) { throw "Folder tree missing." }
    winapp ui wait-for "ItemsList" -a $AppPid -t 3000
    if ($LASTEXITCODE -ne 0) { throw "Items list missing." }
    winapp ui wait-for "StorageTreemap" -a $AppPid -t 3000
}
Test-UI "Treemap renders rectangles for the top rows" {
    winapp ui wait-for "TreemapItem_22222222222222222222222222222221" -a $AppPid -t 3000
    if ($LASTEXITCODE -ne 0) { throw "Projects rectangle missing." }
    winapp ui wait-for "TreemapItem_22222222222222222222222222222222" -a $AppPid -t 3000
}
Test-UI "Table exposes an addressable row per item" {
    winapp ui wait-for "Row_22222222222222222222222222222221" -a $AppPid -t 3000
}
Test-UI "Column headers are sortable controls" {
    winapp ui wait-for "SortNameButton" -a $AppPid -t 3000
    if ($LASTEXITCODE -ne 0) { throw "Name header missing." }
    winapp ui wait-for "SortLogicalButton" -a $AppPid -t 3000
    if ($LASTEXITCODE -ne 0) { throw "Logical header missing." }
    winapp ui wait-for "SortContextButton" -a $AppPid -t 3000
}
Save-StateScreenshot "01-baseline-full"

Test-UI "Selecting a row fills the details pane" {
    winapp ui invoke "TreemapItem_22222222222222222222222222222222" -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "Virtual machines rectangle was not invokable." }
    winapp ui wait-for "InspectorNameText" -a $AppPid --value "Virtual machines" -t 3000
}
Test-UI "Details surface sparse allocation" {
    winapp ui wait-for "InspectorLogicalText" -a $AppPid --value "1.30 TB" -t 3000
    if ($LASTEXITCODE -ne 0) { throw "Logical size missing from details." }
    winapp ui wait-for "InspectorShareText" -a $AppPid --value "30.1%" -t 3000
}
Test-UI "Details keep physical identity above the fold" {
    winapp ui wait-for "InspectorIdentityText" -a $AppPid --value "Folder" --contains -t 3000
    if ($LASTEXITCODE -ne 0) { throw "Physical identity line missing from details." }
    winapp ui wait-for "InspectorIdentityText" -a $AppPid --value "8 items" --contains -t 3000
    if ($LASTEXITCODE -ne 0) { throw "Item count missing from the identity line." }
    winapp ui wait-for "InspectorModifiedText" -a $AppPid --value "Modified" --contains -t 3000
}
Save-StateScreenshot "02-cross-pane-selection"

Test-UI "Tree navigation updates the scope" {
    winapp ui invoke "FolderTreeItem_22222222222222222222222222222221" -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "Projects tree item was not invokable." }
    winapp ui wait-for "ScopeStatusText" -a $AppPid --value "286 GB" --contains -t 3000
}
Test-UI "Up button returns to the drive root" {
    winapp ui invoke "UpButton" -a $AppPid
    winapp ui wait-for "ScopeStatusText" -a $AppPid --value "712 GB" --contains -t 3000
}
Test-UI "Largest files mode switches" {
    winapp ui invoke "LargestFilesModeItem" -a $AppPid
    winapp ui wait-for "Row_33333333333333333333333333333333" -a $AppPid -t 3000
}
Test-UI "Search filters immediately" {
    winapp ui invoke "PrototypeControlsExpander" -a $AppPid
    winapp ui wait-for "ApplySearchTestButton" -a $AppPid -t 3000
    if ($LASTEXITCODE -ne 0) { throw "Prototype test hooks did not expand." }
    winapp ui invoke "ApplySearchTestButton" -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "Could not apply deterministic search." }
    Start-Sleep -Milliseconds 400
    winapp ui wait-for "TreemapItem_33333333333333333333333333333333" -a $AppPid -t 3000
}
Test-UI "Sortable size column works" {
    winapp ui invoke "SortSizeButton" -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "Size header was not invokable." }
    winapp ui invoke "SortNameButton" -a $AppPid
}
Save-StateScreenshot "03-largest-search-sort"

# ------------------------------------------------------------ scan states ---

Test-UI "Prototype controls expand" {
    winapp ui wait-for "ScenarioComboBox" -a $AppPid -t 3000
}
Test-UI "Scanning can be cancelled" {
    Select-ComboOption "ScenarioComboBox" "Scanning / cancel"
    winapp ui wait-for "CancelScanButton" -a $AppPid -t 3000
    if ($LASTEXITCODE -ne 0) { throw "Cancel button did not appear." }
    winapp ui invoke "CancelScanButton" -a $AppPid
    winapp ui wait-for "ScanStatusText" -a $AppPid --value "cancelled" --contains -t 3000
}
Save-StateScreenshot "04-scan-cancelled"

Test-UI "Partial coverage is explicit" {
    Select-ComboOption "ScenarioComboBox" "Partial coverage"
    winapp ui wait-for "CoverageStatusText" -a $AppPid --value "91% coverage" --contains -t 4000
}
Save-StateScreenshot "05-partial-coverage"

Test-UI "Failure exposes retry" {
    Select-ComboOption "ScenarioComboBox" "Failure / retry"
    winapp ui wait-for "RetryScanButton" -a $AppPid -t 5000
    if ($LASTEXITCODE -ne 0) { throw "Retry affordance missing." }
    winapp ui wait-for "ErrorInfoBar" -a $AppPid -t 3000
}
Save-StateScreenshot "06-failure"
Test-UI "Retry recovers" {
    winapp ui invoke "RetryScanButton" -a $AppPid
    winapp ui wait-for "ErrorInfoBar" -a $AppPid --gone -t 5000
}

Test-UI "Empty state is successful" {
    Select-ComboOption "ScenarioComboBox" "Empty drive"
    winapp ui wait-for "EmptyStateText" -a $AppPid --value "Nothing to show here" -t 4000
}
Test-UI "Empty scope also empties the map" {
    winapp ui wait-for "TreemapEmptyText" -a $AppPid -t 3000
}
Save-StateScreenshot "07-empty"

# --------------------------------------------------------- provider states ---

Test-UI "No-provider scenario remains inspectable" {
    Select-ComboOption "ScenarioComboBox" "No provider metadata"
    winapp ui wait-for "TreemapItem_22222222222222222222222222222221" -a $AppPid -t 6000
    if ($LASTEXITCODE -ne 0) { throw "Treemap did not repaint for the no-provider scenario." }
    winapp ui invoke "TreemapItem_22222222222222222222222222222221" -a $AppPid
    winapp ui wait-for "InspectorProviderText" -a $AppPid --value "No provider metadata" -t 4000
}
Test-UI "Stale provider scenario loads" {
    Select-ComboOption "ScenarioComboBox" "Stale metadata"
    winapp ui wait-for "ScopeStatusText" -a $AppPid --value "712 GB" --contains -t 4000
}
Test-UI "Unavailable provider scenario loads" {
    Select-ComboOption "ScenarioComboBox" "Provider unavailable"
    winapp ui wait-for "ScopeStatusText" -a $AppPid --value "712 GB" --contains -t 4000
}

Test-UI "Awkward names render" {
    Select-ComboOption "ScenarioComboBox" "Awkward names"
    winapp ui wait-for "Row_a2222222222222222222222222222221" -a $AppPid -t 4000
}
Save-StateScreenshot "08-awkward-names"

Test-UI "Large deterministic set projects" {
    Select-ComboOption "ScenarioComboBox" "Large generated set"
    winapp ui wait-for "ItemsList" -a $AppPid -t 8000
    if ($LASTEXITCODE -ne 0) { throw "Table did not survive the large scenario." }
    winapp ui invoke "LargestFilesModeItem" -a $AppPid 2>$null | Out-Null
    winapp ui wait-for "ScopeStatusText" -a $AppPid --value "2,540 items" --contains -t 8000
}
Save-StateScreenshot "09-large-virtualized"

Test-UI "Changed refresh stays coherent" {
    Select-ComboOption "ScenarioComboBox" "Changed refresh"
    winapp ui wait-for "TreemapItem_22222222222222222222222222222222" -a $AppPid -t 6000
    if ($LASTEXITCODE -ne 0) { throw "Pre-refresh snapshot did not project." }
    winapp ui invoke "TreemapItem_22222222222222222222222222222222" -a $AppPid
    winapp ui wait-for "InspectorNameText" -a $AppPid --value "Virtual machines" -t 4000
    if ($LASTEXITCODE -ne 0) { throw "Could not select the item that the refresh removes." }

    winapp ui invoke "RefreshButton" -a $AppPid
    winapp ui wait-for "ScopeStatusText" -a $AppPid --value "498 GB" --contains -t 8000
    if ($LASTEXITCODE -ne 0) { throw "Refreshed total did not land." }
    winapp ui wait-for "Row_22222222222222222222222222222221" -a $AppPid -t 4000
    if ($LASTEXITCODE -ne 0) { throw "Refreshed folder is missing from the table." }
    winapp ui wait-for "InspectorEmptyText" -a $AppPid -t 4000
}

# ------------------------------------------------------- layout and theme ---

Test-UI "Baseline restores for layout checks" {
    Select-ComboOption "ScenarioComboBox" "Baseline · 712 GB"
    winapp ui wait-for "ScopeStatusText" -a $AppPid --value "712 GB" --contains -t 5000
}
Test-UI "Compact 1280x800 layout uses inspector drawer" {
    Select-ComboOption "LayoutComboBox" "1280 x 800"
    winapp ui wait-for "InspectorDrawerButton" -a $AppPid -t 4000
    if ($LASTEXITCODE -ne 0) { throw "Drawer button did not appear in compact layout." }
    winapp ui invoke "InspectorDrawerButton" -a $AppPid
    winapp ui wait-for "CloseInspectorButton" -a $AppPid -t 3000
}
Save-StateScreenshot "10-compact-inspector-drawer"
Test-UI "Compact inspector closes" {
    winapp ui invoke "CloseInspectorButton" -a $AppPid
    Start-Sleep -Milliseconds 300
    winapp ui wait-for "ItemsList" -a $AppPid -t 3000
}
Save-StateScreenshot "10b-compact-closed"

Test-UI "Full 1600x900 layout restores the docked details pane" {
    Select-ComboOption "LayoutComboBox" "1600 x 900"
    winapp ui wait-for "TreemapItem_22222222222222222222222222222221" -a $AppPid -t 5000
    if ($LASTEXITCODE -ne 0) { throw "Treemap did not repaint after restoring the full layout." }
    winapp ui invoke "TreemapItem_22222222222222222222222222222221" -a $AppPid
    winapp ui wait-for "InspectorNameText" -a $AppPid --value "Projects" -t 4000
}
Save-StateScreenshot "11-full-layout"

Test-UI "Dark theme switches at runtime" {
    Select-ComboOption "ThemeComboBox" "Dark"
    winapp ui wait-for "ScopeStatusText" -a $AppPid -t 3000
}
Save-StateScreenshot "12-dark-theme"
Test-UI "Light theme switches at runtime" {
    Select-ComboOption "ThemeComboBox" "Light"
    winapp ui wait-for "ScopeStatusText" -a $AppPid -t 3000
}
Save-StateScreenshot "13-light-theme"
Test-UI "System theme restores" {
    Select-ComboOption "ThemeComboBox" "System"
    winapp ui wait-for "ScopeStatusText" -a $AppPid -t 3000
}

# ------------------------------------------------------------ a11y sweep ---

$allElements = @()
try {
    $inspection = winapp ui inspect -a $AppPid --interactive --json 2>$null | ConvertFrom-Json
    $allElements = @($inspection.elements)
}
catch {
    $allElements = @()
}
$appControls = @($allElements | Where-Object {
    $_.type -match "Button|TextBox|ComboBox|CheckBox|ToggleSwitch|TabItem|Edit" -and
    $_.name -notmatch "Minimize|Maximize|Close|System" -and
    $_.className -notmatch "ScrollBar|RepeatButton|PickerHost|#32770|CabinetWClass"
})
$missingIds = @($appControls | Where-Object { -not $_.automationId })
if ($allElements.Count -eq 0) {
    $fail++
    $results += [ordered]@{
        name = "Accessibility tree available"
        status = "FAIL"
        detail = "winapp inspect returned no elements."
    }
}
elseif ($missingIds.Count -eq 0) {
    $pass++
    $results += [ordered]@{
        name = "Interactive controls have AutomationIds"
        status = "PASS"
    }
}
else {
    $fail++
    $missing = ($missingIds | ForEach-Object { "$($_.type) '$($_.name)'" }) -join ", "
    $results += [ordered]@{
        name = "Interactive controls have AutomationIds"
        status = "FAIL"
        detail = "Missing: $missing"
    }
}

$report = [ordered]@{
    schemaVersion = 1
    appPid = $AppPid
    passed = $pass
    failed = $fail
    screenshots = $screenshots
    visualChecklist = [ordered]@{
        requiresHumanReview = $true
        checks = @(
            "No unintended scrollbars",
            "No clipping or overlap",
            "Right-edge controls visible",
            "Tree, table, and treemap remain useful",
            "Treemap rectangles are squarish, not slivers",
            "Row colour dots match treemap rectangles",
            "Light and dark resources render correctly",
            "High Contrast uses system brushes"
        )
    }
    results = $results
}

$report | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $scriptRoot "test-results.json")
Write-Host "`nPassed: $pass | Failed: $fail"
$results | Where-Object { $_.status -eq "FAIL" } | ForEach-Object {
    Write-Host "  FAIL: $($_.name) - $($_.detail)" -ForegroundColor Red
}

if ($fail -gt 0) { exit 1 }
exit 0
