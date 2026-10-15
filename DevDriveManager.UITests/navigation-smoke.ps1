<#
    Read-only shell regression checks, independent of the mutation test seam.
    Does not scan, run benchmarks, create/resize drives, delete data, or open OS settings.
    Optional theme checks change only the app preference and restore its original value.
    Example: .\navigation-smoke.ps1 -AppPid 1234 -IncludeThemeChecks
#>
param(
    [Parameter(Mandatory)][int]$AppPid,
    [switch]$IncludeThemeChecks,
    # Requires an interactive desktop where the app can receive real keyboard input.
    [switch]$IncludeKeyboardChecks,
    [string]$ScreenshotDirectory = (Join-Path $PSScriptRoot 'screenshots\navigation'))

$ErrorActionPreference = 'Stop'
$results = [System.Collections.Generic.List[object]]::new()
# The rail holds three rooms plus Settings, each one page today. A room with more than one page
# would show shell tabs (RoomTabs) and open on its first, so the route loop still checks Tabs.
$routes = @(
    @{ Id = 'NavDashboard'; Name = 'Home'; Anchor = 'OverviewDrives'; Tabs = @() },
    @{ Id = 'NavSpace'; Name = 'Space'; Anchor = 'SpaceFolderTree'; Tabs = @() },
    @{ Id = 'NavDrives'; Name = 'Dev Drive'; Anchor = 'DevDriveScrollViewer'; Tabs = @() },
    @{ Id = 'SettingsItem'; Name = 'Settings'; Anchor = 'ThemeSelector'; Tabs = @() }
)

function Invoke-Ui {
    $output = & winapp ui @args -a $AppPid --json
    if ($LASTEXITCODE -ne 0) { throw "winapp ui $args failed: $output" }
    if ($output) { return ($output | ConvertFrom-Json) }
}

function Test-Navigation([string]$name, [scriptblock]$action) {
    try {
        & $action
        $results.Add(@{ name = $name; status = 'PASS' })
        Write-Host "PASS: $name"
    } catch {
        $results.Add(@{ name = $name; status = 'FAIL'; detail = "$_" })
        Write-Host "FAIL: $name — $_"
    }
}

function Assert-Property([string]$id, [string]$property, [string]$value) {
    Invoke-Ui wait-for $id -p $property --value $value -t 5000 | Out-Null
}

function Assert-Selected([string]$selectedId) {
    foreach ($route in $routes) {
        $expected = if ($route.Id -eq $selectedId) { 'True' } else { 'False' }
        # get-property exposes SelectionItemPattern.IsSelected. ItemStatus is not projected
        # by winapp ui, even though the shell retains it for older automation clients.
        Assert-Property $route.Id 'IsSelected' $expected
    }
}

function Go-To([string]$id, [string]$anchor) {
    Invoke-Ui invoke $id | Out-Null
    Start-Sleep -Milliseconds 300
    Invoke-Ui wait-for $anchor -t 5000 | Out-Null
    Assert-Selected $id
}

# A predicate, not an assertion: absence is a legitimate answer, so it must not throw like Invoke-Ui.
function Test-Present([string]$id, [int]$timeoutMs = 1500) {
    $ErrorActionPreference = 'Continue'
    & winapp ui wait-for $id -a $AppPid -t $timeoutMs 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}

# Space is one page with no tabs, so opening the room is enough.
function Open-SpaceMap {
    Go-To 'NavSpace' 'SpaceFolderTree'
}

# Dev Drive is one page too. Invoking the already-selected room also leaves any sub-page.
function Open-DevDrive {
    Go-To 'NavDrives' 'DevDriveScrollViewer'
}

# Sub-pages keep Dev Drive selected, hide the tabs and swap the title for "Dev Drive > <title>".
function Assert-DevDriveSubPage([string]$title) {
    Assert-Selected 'NavDrives'
    Invoke-Ui wait-for PageBreadcrumb -t 3000 | Out-Null
    Invoke-Ui wait-for 'Dev Drive' --root PageBreadcrumb -t 3000 | Out-Null
    Invoke-Ui wait-for $title --root PageBreadcrumb -t 3000 | Out-Null
    Invoke-Ui wait-for PageTitle --gone -t 1000 | Out-Null
    Invoke-Ui wait-for RoomTabs --gone -t 1000 | Out-Null
}

function Open-DevDriveLink([string]$linkId, [string]$anchor, [string]$title) {
    Open-DevDrive
    Invoke-Ui invoke $linkId | Out-Null
    Start-Sleep -Milliseconds 300
    Invoke-Ui wait-for $anchor -t 5000 | Out-Null
    Assert-DevDriveSubPage $title
}

# Create is the empty state's button on a PC without a Dev Drive, and "Create another" once one exists.
function Open-CreateDevDrive {
    Open-DevDrive
    $trigger = if (Test-Present 'CreateDevDriveButton') { 'CreateDevDriveButton' } else { 'DevDriveCreateAnother' }
    Invoke-Ui invoke $trigger | Out-Null
    Invoke-Ui wait-for CreateFormHead -t 5000 | Out-Null
    Assert-DevDriveSubPage 'Create Dev Drive'
}

# Crumbs are generated from strings and carry no AutomationId: find one by Name and invoke its slug
# straight away (slugs are only valid for the inspect that produced them).
function Invoke-Crumb([string]$name) {
    $tree = Invoke-Ui inspect PageBreadcrumb --depth 6
    $queue = [System.Collections.Generic.Queue[object]]::new()
    foreach ($window in $tree.windows) { foreach ($element in $window.elements) { $queue.Enqueue($element) } }
    $textMatch = $null
    while ($queue.Count -gt 0) {
        $node = $queue.Dequeue()
        if ($node.name -eq $name) {
            if ($node.className -like '*BreadcrumbBarItem') { Invoke-Ui invoke $node.selector | Out-Null; return }
            if (-not $textMatch) { $textMatch = $node }
        }
        foreach ($child in $node.children) { $queue.Enqueue($child) }
    }
    if (-not $textMatch) { throw "No '$name' crumb in PageBreadcrumb." }
    # invoke falls back to the nearest invokable ancestor: the crumb that owns this text.
    Invoke-Ui invoke $textMatch.selector | Out-Null
}

function Expand-SettingsGroup([string]$id) {
    # Toolkit's outer SettingsExpander is a Group; the native Expander child owns UIA expansion.
    $tree = Invoke-Ui inspect $id --depth 2
    $header = $tree.windows[0].elements[0].children |
        Where-Object className -eq 'Microsoft.UI.Xaml.Controls.Expander' | Select-Object -First 1
    if (-not $header) { throw "No native Expander peer for $id." }
    if ($header.expandState -ne 'expanded') {
        Invoke-Ui invoke $header.selector | Out-Null
    }
}

New-Item -ItemType Directory -Path $ScreenshotDirectory -Force | Out-Null
Test-Navigation 'Navigation shell landmark and preserved frame declaration' {
    # NavigationView exposes a Pane peer with no ClassName; its destination peers below expose
    # the native class and SelectionItem pattern, unlike the former hand-built buttons.
    Assert-Property 'ShellNavView' 'Name' 'Rooms'
    # Frame is structural and has no UIA peer. Verify its declaration, then use each page's
    # real UIA anchor below to verify the hosted content, rather than assuming a Frame peer.
    [xml]$shell = Get-Content (Join-Path $PSScriptRoot '../DevDriveManager/ShellPage.xaml')
    if (-not $shell.SelectSingleNode("//*[local-name()='NavigationView']//*[local-name()='Frame' and @*[local-name()='Name']='ContentFrame' and @*[local-name()='AutomationProperties.AutomationId']='ShellContentFrame']")) {
        throw 'NavigationView no longer hosts the existing content frame.'
    }
    [xml]$theme = Get-Content (Join-Path $PSScriptRoot '../DevDriveManager/Themes/StorageManagerTheme.xaml')
    foreach ($key in @('SmSetRowStyle', 'SmSetLabelTextStyle', 'SmSetSubTextStyle', 'SmSetComboStyle')) {
        if (-not $theme.DocumentElement.SelectSingleNode("*[local-name()='Style' and @*[local-name()='Key']='$key']")) {
            throw "Create's shared form style $key must remain available in every theme."
        }
    }
}
Test-Navigation 'Rail: Home, Space and Dev Drive rooms; retired rooms are gone' {
    foreach ($removed in @('NavReclaim', 'NavPackageCaches', 'NavBenchmarks', 'NavCreate')) {
        Invoke-Ui wait-for $removed --gone -t 1000 | Out-Null
    }
}
foreach ($route in $routes) {
    Test-Navigation "$($route.Name): native item, accessible name, route and exclusive selection" {
        Assert-Property $route.Id 'ClassName' 'Microsoft.UI.Xaml.Controls.NavigationViewItem'
        Assert-Property $route.Id 'Name' $route.Name
        Go-To $route.Id $route.Anchor
        # Home draws its own "Home" title (PageTitle); the shell's own title steps aside there.
        $titleId = if ($route.Name -eq 'Settings') { 'SettingsTitle' } else { 'PageTitle' }
        Assert-Property $titleId 'Name' $route.Name
        if ($route.Tabs.Count) {
            Invoke-Ui wait-for RoomTabs -t 3000 | Out-Null
            foreach ($tab in $route.Tabs) { Invoke-Ui wait-for $tab -t 3000 | Out-Null }
            Assert-Property $route.Tabs[0] 'IsSelected' 'True'
        } else {
            Invoke-Ui wait-for RoomTabs --gone -t 1000 | Out-Null
        }
        Invoke-Ui wait-for PageBreadcrumb --gone -t 1000 | Out-Null
        foreach ($removed in @('OverviewVolumeStrip', 'ReclaimVolumeStrip', 'CachesVolumeStrip',
                               'DrivesVolumeStrip', 'BenchmarksVolumeStrip', 'CreateVolumeStrip')) {
            Invoke-Ui wait-for $removed --gone -t 1000 | Out-Null
        }
    }
}
Test-Navigation 'Home: quick actions open Space, Clean up and Package caches' {
    Go-To 'NavDashboard' 'OverviewDrives'
    Invoke-Ui wait-for OverviewQuick_benchmarks --gone -t 1000 | Out-Null
    [xml]$dashboard = Get-Content (Join-Path $PSScriptRoot '..\DevDriveManager\Pages\DashboardPage.xaml')
    $order = @($dashboard.SelectNodes("//*[starts-with(@*[local-name()='AutomationProperties.AutomationId'], 'OverviewQuick_')]") |
        ForEach-Object { $_.GetAttribute('AutomationProperties.AutomationId') })
    if (($order -join ',') -ne 'OverviewQuick_space,OverviewQuick_reclaim,OverviewQuick_caches') {
        throw "Quick actions must read Space, Clean up, Package caches; got '$($order -join ', ')'."
    }
    foreach ($action in @(
            @{ Id = 'OverviewQuick_space'; Room = 'NavSpace'; Tab = $null; Anchor = 'SpaceFolderTree' },
            # "reclaim" is an alias for Space: clean-up is the panel beside the map.
            @{ Id = 'OverviewQuick_reclaim'; Room = 'NavSpace'; Tab = $null; Anchor = 'SpaceCleanupPanel' },
            # "caches" is an alias for Dev Drive: package caches are the panel beside it.
            @{ Id = 'OverviewQuick_caches'; Room = 'NavDrives'; Tab = $null; Anchor = 'DevDriveCachePanel' })) {
        Go-To 'NavDashboard' $action.Id
        Invoke-Ui invoke $action.Id | Out-Null
        Invoke-Ui wait-for $action.Anchor -t 5000 | Out-Null
        Assert-Selected $action.Room
        if ($action.Tab) { Assert-Property $action.Tab 'IsSelected' 'True' }
        else { Invoke-Ui wait-for RoomTabs --gone -t 1000 | Out-Null }
    }
}
Test-Navigation 'Space: one page, the map with what can be cleaned up beside it' {
    Open-SpaceMap
    Invoke-Ui wait-for SpaceCleanupPanel -t 3000 | Out-Null
    Assert-Property 'PageTitle' 'Name' 'Space'
    foreach ($gone in @('RoomTabs', 'Tab_SpaceMap', 'Tab_SpaceCleanUp', 'ReclaimCategoryList')) {
        Invoke-Ui wait-for $gone --gone -t 1000 | Out-Null
    }
    Assert-Selected 'NavSpace'
}
Test-Navigation 'Dev Drive: one page, with recommended caches beside it' {
    Open-DevDrive
    Invoke-Ui wait-for DevDriveCachePanel -t 3000 | Out-Null
    Assert-Property 'PageTitle' 'Name' 'Dev Drive'
    foreach ($gone in @('RoomTabs', 'Tab_DevDriveOverview', 'Tab_PackageCaches', 'PackageCachesScrollViewer',
                        'CachesTableTitle', 'CacheTabStrip', 'DevDriveCachesLink')) {
        Invoke-Ui wait-for $gone --gone -t 1000 | Out-Null
    }
    Assert-Selected 'NavDrives'
}
Test-Navigation 'Dev Drive: offers exactly one way to Create and its links' {
    Open-DevDrive
    foreach ($id in @('DevDriveMoreHead', 'DevDriveCompareSpeedLink', 'DevDriveVolumesLink', 'DevDriveStorageSettingsLink')) {
        Invoke-Ui wait-for $id -t 3000 | Out-Null
    }
    # DevDriveStorageSettingsLink opens Windows Settings, so it is only checked for presence.
    $hasDevDrive = Test-Present 'DevDriveCards'
    if ($hasDevDrive -eq (Test-Present 'DevDriveEmptyState')) { throw 'Show either the empty state or the drive cards.' }
    if ($hasDevDrive -eq (Test-Present 'CreateDevDriveButton')) { throw 'Only the empty state offers Create Dev Drive.' }
    if ($hasDevDrive -ne (Test-Present 'DevDriveCreateAnother')) { throw 'Create another belongs with an existing Dev Drive.' }
    if ($hasDevDrive -ne (Test-Present 'DevDrivePerformanceMode')) { throw 'Performance mode belongs with an existing Dev Drive.' }
    if ($hasDevDrive -ne (Test-Present 'DevDriveSetupHead')) { throw 'The Set up section belongs with an existing Dev Drive.' }
}
Test-Navigation 'Dev Drive: links open sub-pages under a breadcrumb' {
    Open-DevDriveLink 'DevDriveCompareSpeedLink' 'WorkloadsList' 'Compare speed'
    Open-DevDriveLink 'DevDriveVolumesLink' 'VolumesList' 'Drives and filters'
    # Create moved to the Dev Drive page; the volumes table no longer offers it.
    Invoke-Ui wait-for CreateDevDriveButton --gone -t 1000 | Out-Null
}
Test-Navigation 'Create Dev Drive: sub-page of Dev Drive; the first crumb and the rail lead back' {
    Invoke-Ui wait-for NavCreate --gone -t 1000 | Out-Null
    Open-CreateDevDrive
    Invoke-Crumb 'Dev Drive'
    Invoke-Ui wait-for DevDriveScrollViewer -t 5000 | Out-Null
    Invoke-Ui wait-for PageBreadcrumb --gone -t 3000 | Out-Null
    Assert-Property 'PageTitle' 'Name' 'Dev Drive'
    Invoke-Ui wait-for RoomTabs --gone -t 1000 | Out-Null
    Assert-Selected 'NavDrives'
    # Invoking the already-selected room also leaves a sub-page.
    Open-DevDriveLink 'DevDriveCompareSpeedLink' 'WorkloadsList' 'Compare speed'
    Go-To 'NavDrives' 'DevDriveScrollViewer'
    Invoke-Ui wait-for PageBreadcrumb --gone -t 3000 | Out-Null
    Assert-Property 'PageTitle' 'Name' 'Dev Drive'
}
Test-Navigation 'Space: one native hierarchy beside the treemap' {
    Open-SpaceMap
    foreach ($removed in @('SpaceItemsList', 'SpaceViewMode', 'SpaceModeFolders',
                           'SpaceModeLargestFiles', 'SpaceRootName', 'SpaceInspectorTitle', 'SpaceBreadcrumb')) {
        Invoke-Ui wait-for $removed --gone -t 1000 | Out-Null
    }
    foreach ($remaining in @('SpaceTreemap', 'SpaceVolumeStrip', 'ScanBarScanButton')) {
        Invoke-Ui wait-for $remaining -t 3000 | Out-Null
    }
    foreach ($hidden in @('SpaceStatusBar', 'SpaceScanProgress', 'SpaceCancelScan')) {
        Invoke-Ui wait-for $hidden --gone -t 1000 | Out-Null
    }
    Assert-Property 'SpaceVolumeStrip' 'ClassName' 'ComboBox'
    Assert-Property 'ScanBarScanButton' 'Name' 'Scan'
    [xml]$space = Get-Content (Join-Path $PSScriptRoot '..\DevDriveManager\Pages\SpacePage.xaml')
    $picker = $space.SelectSingleNode("//*[local-name()='ComboBox']")
    $scan = $space.SelectSingleNode("//*[local-name()='Button' and @*[local-name()='Name']='ScanButton']")
    if ($picker.GetAttribute('Height') -ne '48' -or $scan.GetAttribute('Height') -ne '48' -or
        -not $picker.SelectSingleNode(".//*[local-name()='Grid' and @RowSpacing='7']")) {
        throw 'Volume picker and Scan must share a 48-DIP height with a clear text/indicator gap.'
    }
    if (-not $picker.SelectSingleNode(".//*[local-name()='TextBlock' and @FontWeight='SemiBold']") -or
        -not $picker.SelectSingleNode(".//*[local-name()='TextBlock' and @*[local-name()='Grid.Column']='1']") -or
        -not $picker.SelectSingleNode(".//*[local-name()='ProgressBar' and @Height='2']")) {
        throw 'Picker needs a semibold name, inline free space and a minimal capacity bar.'
    }
    $toolbar = $space.SelectSingleNode("//*[local-name()='Grid' and @*[local-name()='Name']='ScanToolbar']")
    if ($toolbar.GetAttribute('HorizontalAlignment') -ne 'Left' -or
        $space.SelectSingleNode("//*[local-name()='TextBlock' and @Style='{StaticResource TitleTextBlockStyle}']")) {
        throw 'The shell owns the Space title; the page keeps only its left-aligned scan toolbar.'
    }
    $tree = $space.SelectSingleNode("//*[local-name()='TreeView']")
    $map = $space.SelectSingleNode("//*[local-name()='TreemapPane']")
    if ($tree.ParentNode -ne $map.ParentNode.ParentNode -or $map.ParentNode.GetAttribute('Grid.Column') -ne '1') {
        throw 'The hierarchy and treemap must be siblings in left/right columns.'
    }
    if ($space.OuterXml -match 'SmCard|PHYSICAL TREE|SelectorBar|ListView') {
        throw 'Space must not restore custom cards, headers or the duplicate table.'
    }
    if ($tree.ParentNode.GetAttribute('Background') -ne '{ThemeResource CardBackgroundFillColorDefaultBrush}' -or
        $tree.ParentNode.GetAttribute('BorderBrush') -ne '{ThemeResource CardStrokeColorDefaultBrush}') {
        throw 'Tree and treemap must share one native card.'
    }
    if (-not $tree.SelectSingleNode(".//*[local-name()='Grid' and @Margin='0,0,20,0']") -or
        -not $tree.SelectSingleNode(".//*[local-name()='FontIcon' and @Glyph='{x:Bind local:SpacePage.NodeGlyph(IsFolder)}']")) {
        throw 'Tree icons must distinguish folders/files and retain the scrollbar gutter.'
    }
    foreach ($animation in @('Implicit.ShowAnimations', 'Implicit.HideAnimations')) {
        if (-not $space.SelectSingleNode("//*[local-name()='$animation']")) {
            throw "Toolkit $animation missing."
        }
        $bar = $space.SelectSingleNode("//*[local-name()='Border' and @*[local-name()='Name']='SpaceStatusBar']")
        if ($bar.GetAttribute('Grid.Row') -ne $tree.ParentNode.GetAttribute('Grid.Row') -or
            $bar.GetAttribute('HorizontalAlignment') -ne 'Center' -or $bar.GetAttribute('MaxWidth') -ne '560') {
            throw 'Scan progress must float centered over the content, not reserve a footer row.'
        }
        if (-not $bar.SelectSingleNode(".//*[local-name()='TextBlock' and @FontSize='12']") -or
            -not $bar.SelectSingleNode(".//*[local-name()='Button' and @Content='Cancel' and @*[local-name()='Grid.Column']='2']")) {
            throw 'Floating progress needs 12-DIP status text and an inline Cancel button.'
        }
    }
    foreach ($binding in @('MaterializedChildren', 'HasUnrealizedChildren', 'PaletteBrush(ColorIndex)', 'SizeDisplay')) {
        if (-not $tree.OuterXml.Contains($binding)) { throw "Tree binding missing: $binding" }
    }
}
Test-Navigation 'Settings: native layout, preserved inputs and removed table/footer' {
    Go-To 'SettingsItem' 'ThemeSelector'
    [xml]$settings = Get-Content (Join-Path $PSScriptRoot '../DevDriveManager/Pages/SettingsPage.xaml')
    $ns = [System.Xml.XmlNamespaceManager]::new($settings.NameTable)
    $ns.AddNamespace('t', 'using:CommunityToolkit.WinUI.Controls')
    foreach ($header in @('App theme', 'Low free-space warning', 'Combine cache signals after',
                          'Keep readings for', 'Saved history', 'Default creation method',
                          'Source')) {
        if (-not $settings.SelectSingleNode("//t:SettingsCard[@Header='$header']", $ns)) {
            throw "Native settings card '$header' missing."
        }
    }
    if ($settings.SelectNodes('//t:SettingsExpander', $ns).Count -ne 3) { throw 'Settings expanders missing.' }
    if (-not $settings.SelectSingleNode("//t:SettingsExpander[@Header='DevDriveManager']/t:SettingsExpander.Items/t:SettingsCard[@Header='Source' and @IsClickEnabled='True' and @Click='Source_Click']", $ns)) {
        throw 'Clickable Source card missing from About expander.'
    }
    if (-not $settings.SelectSingleNode("//*[local-name()='StackPanel' and @MaxWidth='1000']/*[last()][local-name()='HyperlinkButton' and @Click='ResetPreferences_Click']")) {
        throw 'Reset preferences link missing from the bottom of Settings.'
    }
    if (-not $settings.SelectSingleNode("//*[local-name()='StackPanel' and @MaxWidth='1000' and @Spacing='4']")) {
        throw 'Centered 1000-DIP settings column with 4-DIP card gaps missing.'
    }
    Invoke-Ui wait-for WriteSurfaceHead --gone -t 1000 | Out-Null
    Invoke-Ui wait-for SettingsStatusBar --gone -t 1000 | Out-Null
    Invoke-Ui wait-for SettingsLearnWhatThisDoes --gone -t 1000 | Out-Null
    Invoke-Ui wait-for AppVersionLine -t 3000 | Out-Null
    Expand-SettingsGroup AboutSettingsExpander
    Invoke-Ui wait-for RepoLink -t 3000 | Out-Null
    Invoke-Ui wait-for ResetPreferencesButton -t 3000 | Out-Null
    foreach ($id in @('WatchCachesToggle', 'LowFreeCombo', 'CreationMethodCombo')) {
        Invoke-Ui wait-for $id -t 3000 | Out-Null
    }
    Expand-SettingsGroup CacheSignalsExpander
    Invoke-Ui wait-for RollupCombo -t 3000 | Out-Null
    Invoke-Ui wait-for HistorySettingsExpander --gone -t 1000 | Out-Null
}
Test-Navigation 'Settings: warning preference persists across navigation and restores original value' {
    Go-To 'SettingsItem' 'ThemeSelector'
    $original = (Invoke-Ui get-value LowFreeCombo).text
    $options = @{ '10%' = 'LowFree10'; '15%' = 'LowFree15'; '20%' = 'LowFree20'; '25%' = 'LowFree25' }
    if (-not $options.ContainsKey($original)) { throw "Unsupported original threshold '$original'; not changing it." }
    $target = if ($original -eq '25%') { '20%' } else { '25%' }
    try {
        Invoke-Ui invoke LowFreeCombo | Out-Null
        Invoke-Ui invoke $options[$target] | Out-Null
        Go-To 'NavDrives' 'DevDriveScrollViewer'
        Go-To 'SettingsItem' 'ThemeSelector'
        Invoke-Ui wait-for LowFreeCombo --value $target -t 3000 | Out-Null
    } finally {
        Go-To 'SettingsItem' 'ThemeSelector'
        Invoke-Ui invoke LowFreeCombo | Out-Null
        Invoke-Ui invoke $options[$original] | Out-Null
        Invoke-Ui wait-for LowFreeCombo --value $original -t 3000 | Out-Null
    }
}
Test-Navigation 'Settings omits machine action shortcuts' {
    Go-To 'SettingsItem' 'ThemeSelector'
    foreach ($id in @('RescanButton', 'SettingsCreateButton', 'SettingsManageInStorageButton', 'SettingsOpenWindowsSecurityButton')) {
        Invoke-Ui wait-for $id --gone -t 3000 | Out-Null
    }
    Invoke-Ui wait-for CreationMethodCombo -t 3000 | Out-Null
    Assert-Selected 'SettingsItem'
}
if ($IncludeKeyboardChecks) {
    Test-Navigation 'Keyboard Down and Enter navigates to the next room' {
        Go-To 'NavDashboard' 'OverviewDrives'
        Invoke-Ui send-keys 'down enter' --target NavDashboard --via send-input | Out-Null
        Invoke-Ui wait-for SpaceFolderTree -t 5000 | Out-Null
        Assert-Selected 'NavSpace'
        Invoke-Ui screenshot -o (Join-Path $ScreenshotDirectory 'keyboard-focus.png') | Out-Null
    }
}

if ($IncludeThemeChecks) {
    Go-To 'SettingsItem' 'ThemeSelector'
    $originalTheme = (Invoke-Ui get-value ThemeSelector).text
    $themeOptions = @{
        'System default' = 'ThemeOptionSystem'
        'Light' = 'ThemeOptionLight'
        'Dark' = 'ThemeOptionDark'
    }
    if (-not $themeOptions.ContainsKey($originalTheme)) { throw "Unknown app theme '$originalTheme'; refusing to change it." }
    try {
        foreach ($theme in @('Light', 'Dark')) {
            Test-Navigation "$theme theme and selected footer item" {
                Invoke-Ui invoke ThemeSelector | Out-Null
                Invoke-Ui invoke $themeOptions[$theme] | Out-Null
                Invoke-Ui wait-for ThemeSelector --value $theme -t 5000 | Out-Null
                Assert-Selected 'SettingsItem'
                # A selected ComboBox value is not proof that pixels have repainted. Give theme
                # propagation/brush transitions time to settle, then review these captures.
                Start-Sleep -Milliseconds 500
                Invoke-Ui screenshot -o (Join-Path $ScreenshotDirectory "$theme.png") | Out-Null
            }
        }
    } finally {
        Invoke-Ui invoke ThemeSelector | Out-Null
        Invoke-Ui invoke $themeOptions[$originalTheme] | Out-Null
        Invoke-Ui wait-for ThemeSelector --value $originalTheme -t 5000 | Out-Null
    }
}

Open-SpaceMap
Invoke-Ui screenshot -o (Join-Path $ScreenshotDirectory 'space.png') | Out-Null
$results | ConvertTo-Json
$failed = @($results | Where-Object status -eq 'FAIL').Count
Write-Host "$($results.Count - $failed) passed; $failed failed. Screenshots: $ScreenshotDirectory"
if ($failed) { exit 1 }
