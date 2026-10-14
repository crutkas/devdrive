<#
    Run ONLY against a fresh Debug launch with --space-ui-fixture. Refuses live data.
    Exercises synthetic scans, never filesystem scans or storage mutations.
#>
param([Parameter(Mandatory)][int]$AppPid)
$ErrorActionPreference = 'Stop'

function Ui {
    $output = & winapp ui @args -a $AppPid --json
    if ($LASTEXITCODE -ne 0) { throw "winapp ui $args failed: $output" }
    if ($output) { $output | ConvertFrom-Json }
}
function Wait-Idle {
    Ui wait-for SpaceStatusBar --gone -t 15000 | Out-Null
    Ui wait-for ScanBarScanButton -p IsEnabled --value True -t 3000 | Out-Null
    Ui wait-for SpaceCancelScan --gone -t 3000 | Out-Null
    Ui wait-for SpaceVolumeStrip -p IsEnabled --value True -t 3000 | Out-Null
}
function Pick([string]$name) {
    Ui invoke SpaceVolumeStrip | Out-Null
    $tree = Ui inspect SpaceVolumeStrip --depth 3
    $items = @($tree.windows[0].elements[0].children | Where-Object type -eq ListItem)
    if ($items.Count -ne 6 -or @($items | Where-Object { $_.name -notlike 'Synthetic *' }).Count) {
        throw 'Refusing to scan: picker does not contain only the six synthetic fixture volumes.'
    }
    $item = $items | Where-Object name -eq $name
    if (-not $item) { throw "Fixture '$name' not found." }
    Ui invoke $item.selector | Out-Null
    Ui wait-for SpaceStatusBar --gone -t 1000 | Out-Null
}
function Start-Scan {
    Ui invoke ScanBarScanButton | Out-Null
    Ui wait-for SpaceScanProgress -t 3000 | Out-Null
    Ui wait-for SpaceCancelScan -t 3000 | Out-Null
    Ui wait-for SpaceCancelProgressRing -t 3000 | Out-Null
    Ui wait-for SpaceVolumeStrip -p IsEnabled --value False -t 2000 | Out-Null
    Ui wait-for ScanBarScanButton --gone -t 2000 | Out-Null
}

Ui invoke NavSpace | Out-Null
Ui wait-for SpaceOverlayTitle --value 'Synthetic space test data — no disk scans' -t 5000 | Out-Null
Wait-Idle
$header = (Ui inspect SpaceHeader --depth 0).windows[0].elements[0]
$title = (Ui inspect PageTitle --depth 0).windows[0].elements[0]
$scan = (Ui inspect ScanBarScanButton --depth 0).windows[0].elements[0]
$scale = $scan.height / 48
if ([Math]::Abs(($scan.x + $scan.width) - ($header.x + $header.width)) -gt 2) {
    throw 'Scan action is not right-aligned with the page header.'
}
if ($header.width -ge 580 * $scale -and
    [Math]::Abs(($scan.y + $scan.height / 2) - ($title.y + $title.height / 2)) -gt 2) {
    throw 'Space title and scan actions do not share one desktop header row.'
}
Ui invoke SpaceVolumeStrip | Out-Null
$picker = Ui inspect SpaceVolumeStrip --depth 3
$first = $picker.windows[0].elements[0].children |
    Where-Object name -eq 'Synthetic complete (A:)' | Select-Object -First 1
if (-not $first) { throw 'Synthetic volume template was not found.' }
$name = $first.children | Where-Object { $_.type -eq 'Text' -and $_.name -eq 'Synthetic complete (A:)' }
$capacity = $first.children | Where-Object type -eq ProgressBar
if (-not $name -or -not $capacity -or $capacity.y - ($name.y + $name.height) -lt 6 * $scale) {
    throw 'Capacity indicator must have at least 6 DIPs of space below the text row.'
}
Ui invoke $first.selector | Out-Null
Pick 'Synthetic slow scan (D:)'
$selection = (Ui get-value SpaceVolumeStrip).text
Ui invoke NavDrives | Out-Null
Ui invoke NavSpace | Out-Null
if ((Ui get-value SpaceVolumeStrip).text -ne $selection) { throw 'Unscanned selection was not preserved.' }
$beforeTree = (Ui inspect SpaceFolderTree --depth 0).windows[0].elements[0]
Start-Scan
$duringTree = (Ui inspect SpaceFolderTree --depth 0).windows[0].elements[0]
if ($beforeTree.height -ne $duringTree.height) { throw 'Floating progress resized the explorer content.' }
$bar = (Ui inspect SpaceStatusBar --depth 2).windows[0].elements[0]
$header = (Ui inspect SpaceHeader --depth 0).windows[0].elements[0]
$cancel = (Ui inspect SpaceCancelScan --depth 0).windows[0].elements[0]
$ring = (Ui inspect SpaceCancelProgressRing --depth 0).windows[0].elements[0]
$progress = (Ui inspect SpaceScanProgress --depth 0).windows[0].elements[0]
$scale = $cancel.height / 48
if ($bar.width -gt 560 * $scale + 1 -or
    [Math]::Abs(($bar.x + $bar.width / 2) - ($header.x + $header.width / 2)) -gt 2) {
    throw 'Progress overlay is not centered or exceeds its 560-DIP maximum.'
}
if ($cancel.name -ne 'Cancel' -or
    [Math]::Abs(($cancel.x + $cancel.width) - ($scan.x + $scan.width)) -gt 2 -or
    [Math]::Abs(($cancel.y + $cancel.height / 2) - ($scan.y + $scan.height / 2)) -gt 2) {
    throw 'Cancel did not replace Scan in the header.'
}
if ($ring.x -lt $cancel.x -or $ring.x + $ring.width -gt $cancel.x + $cancel.width -or
    [Math]::Abs(($ring.y + $ring.height / 2) - ($cancel.y + $cancel.height / 2)) -gt 2) {
    throw 'The progress ring is not inside the Cancel button.'
}
if ($progress.y -le $header.y + $header.height) {
    throw 'The scan progress panel overlaps the header.'
}
Ui invoke NavDrives | Out-Null
Ui invoke NavSpace | Out-Null
Ui wait-for SpaceScanProgress -t 3000 | Out-Null
Ui wait-for SpaceVolumeStrip -p IsEnabled --value False -t 2000 | Out-Null
Ui invoke SpaceCancelScan | Out-Null
Wait-Idle
Ui wait-for SpaceOverlayTitle --value 'Scan cancelled' -t 3000 | Out-Null
# Rapid cancellation/restart must not leave the implicit hide's opacity on the next show.
Start-Scan
Ui invoke SpaceCancelScan | Out-Null
Wait-Idle
Write-Host 'PASS: selection, ongoing scan navigation, cancellation and rapid restart'

Pick 'Synthetic complete (A:)'
Start-Scan
Wait-Idle
Ui wait-for SpaceCoverageNotice --gone -t 1000 | Out-Null
$tree = Ui inspect SpaceFolderTree --depth 3
if (-not ($tree | ConvertTo-Json -Depth 15).Contains('FolderTreeItem_')) { throw 'Completed scan has no hierarchy.' }
Ui invoke NavDrives | Out-Null
Ui invoke NavSpace | Out-Null
Ui wait-for SpaceOverlayTitle --gone -t 1000 | Out-Null
Write-Host 'PASS: completion and shared hierarchy navigation'

Pick 'Synthetic partial (B:)'
Start-Scan
Wait-Idle
Ui wait-for SpaceCoverageNotice -t 3000 | Out-Null
$coverage = Ui inspect SpaceCoverageNotice --depth 3
if (-not ($coverage | ConvertTo-Json -Depth 10).Contains('denied')) { throw 'Partial coverage warning disappeared.' }
Write-Host 'PASS: partial coverage persists after the progress bar hides'

Pick 'Synthetic failure (C:)'
Start-Scan
Wait-Idle
Ui wait-for SpaceOverlayTitle --value 'That scan did not finish' -t 3000 | Out-Null
Ui invoke SpaceRetryScan | Out-Null
Ui wait-for SpaceScanProgress -t 3000 | Out-Null
Wait-Idle
Ui wait-for SpaceOverlayTitle --gone -t 1000 | Out-Null
Write-Host 'PASS: failure and retry'

Pick 'Synthetic unknown capacity (F:)'
Ui invoke SpaceVolumeStrip | Out-Null
$picker = Ui inspect SpaceVolumeStrip --depth 4
$unknown = $picker.windows[0].elements[0].children | Where-Object name -eq 'Synthetic unknown capacity (F:)'
if (@($unknown.children | Where-Object type -eq ProgressBar).Count) { throw 'Unknown capacity was drawn as measured.' }
if (-not ($unknown | ConvertTo-Json -Depth 10).Contains('Free space unavailable')) { throw 'Unknown capacity not explained.' }
Ui invoke $unknown.selector | Out-Null
Start-Scan
Wait-Idle
Ui wait-for SpaceOverlayTitle --value 'No files found' -t 3000 | Out-Null
Write-Host 'PASS: unknown capacity and empty scan'

Pick 'Synthetic large tree (E:)'
Start-Scan
Wait-Idle
Ui invoke FolderTreeItem_90000000000000000001000000010009 | Out-Null
$tree = Ui inspect SpaceFolderTree --depth 4
$rows = @($tree.windows[0].elements[0].children[0].children |
    Where-Object { $_.type -eq 'TreeItem' -and -not $_.isOffscreen -and $_.width -gt 0 })
if (-not @($rows | Where-Object name -like '*, file,*').Count) { throw 'Expanding a synthetic folder did not materialize files.' }
foreach ($row in $rows) {
    $size = @($row.children | Where-Object type -eq Text) | Select-Object -Last 1
    if ($size -and ($size.x + $size.width) -gt ($row.x + $row.width - 19)) {
        throw "Scrollbar gutter lost for '$($row.name)'."
    }
}
Write-Host 'PASS: large hierarchy, file materialization and 20-DIP scrollbar gutter'
