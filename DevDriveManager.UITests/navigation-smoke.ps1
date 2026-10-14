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
$routes = @(
    @{ Id = 'NavDashboard'; Name = 'Overview'; Anchor = 'SignalsSubtitle' },
    @{ Id = 'NavReclaim'; Name = 'Reclaim'; Anchor = 'ReclaimCategoryList' },
    @{ Id = 'NavSpace'; Name = 'Space'; Anchor = 'SpaceFolderTree' },
    @{ Id = 'NavPackageCaches'; Name = 'Package caches'; Anchor = 'PackageCachesScrollViewer' },
    @{ Id = 'NavDrives'; Name = 'Drives'; Anchor = 'VolumesList' },
    @{ Id = 'NavBenchmarks'; Name = 'Benchmarks'; Anchor = 'WorkloadsList' },
    @{ Id = 'NavCreate'; Name = 'Create Dev Drive'; Anchor = 'CreateFormHead' },
    @{ Id = 'SettingsItem'; Name = 'Settings'; Anchor = 'ThemeSelector' }
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
foreach ($route in $routes) {
    Test-Navigation "$($route.Name): native item, accessible name, route and exclusive selection" {
        Assert-Property $route.Id 'ClassName' 'Microsoft.UI.Xaml.Controls.NavigationViewItem'
        Assert-Property $route.Id 'Name' $route.Name
        Go-To $route.Id $route.Anchor
        $titleId = if ($route.Name -eq 'Settings') { 'SettingsTitle' } else { 'PageTitle' }
        Assert-Property $titleId 'Name' $route.Name
        foreach ($removed in @('OverviewVolumeStrip', 'ReclaimVolumeStrip', 'CachesVolumeStrip',
                               'DrivesVolumeStrip', 'BenchmarksVolumeStrip', 'CreateVolumeStrip')) {
            Invoke-Ui wait-for $removed --gone -t 1000 | Out-Null
        }
    }
}
Test-Navigation 'Space: one native hierarchy beside the treemap' {
    Go-To 'NavSpace' 'SpaceFolderTree'
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
    if ($toolbar.GetAttribute('HorizontalAlignment') -ne 'Right' -or
        -not $toolbar.ParentNode.SelectSingleNode("*[local-name()='TextBlock' and @Style='{StaticResource TitleTextBlockStyle}']")) {
        throw 'Space title and right-aligned scan actions must share one responsive header.'
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
    foreach ($id in @('WatchCachesToggle', 'RecordHistoryToggle', 'LowFreeCombo', 'CreationMethodCombo')) {
        Invoke-Ui wait-for $id -t 3000 | Out-Null
    }
    Expand-SettingsGroup CacheSignalsExpander
    Expand-SettingsGroup HistorySettingsExpander
    foreach ($id in @('RollupCombo', 'RetentionCombo', 'ClearHistoryButton')) {
        Invoke-Ui wait-for $id -t 3000 | Out-Null
    }
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
        Go-To 'NavDrives' 'VolumesList'
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
        Go-To 'NavDashboard' 'SignalsSubtitle'
        Invoke-Ui send-keys 'down enter' --target NavDashboard --via send-input | Out-Null
        Invoke-Ui wait-for ReclaimCategoryList -t 5000 | Out-Null
        Assert-Selected 'NavReclaim'
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

Go-To 'NavSpace' 'SpaceFolderTree'
Invoke-Ui screenshot -o (Join-Path $ScreenshotDirectory 'space.png') | Out-Null
$results | ConvertTo-Json
$failed = @($results | Where-Object status -eq 'FAIL').Count
Write-Host "$($results.Count - $failed) passed; $failed failed. Screenshots: $ScreenshotDirectory"
if ($failed) { exit 1 }
