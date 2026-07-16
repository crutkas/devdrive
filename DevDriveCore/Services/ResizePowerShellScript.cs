using System.Text;
using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Builds the read-only snapshot and destructive resize scripts used by the elevated resize helper.
/// Keeping script generation in the core makes the exact privileged command sequence unit-testable.
/// </summary>
internal static class ResizePowerShellScript
{
    internal const string MutationStartedMarker = "__DDM_RESIZE_MUTATION_STARTED__";

    internal static string BuildSnapshot(char letter)
    {
        char source = char.ToUpperInvariant(letter);
        if (source is < 'A' or > 'Z')
        {
            throw new ArgumentOutOfRangeException(nameof(letter));
        }

        return $$"""
$ErrorActionPreference='Stop'
try {
  $p = Get-Partition -DriveLetter '{{source}}' -ErrorAction Stop
  $d = Get-Disk -Number $p.DiskNumber -ErrorAction Stop
  $v = Get-Volume -DriveLetter '{{source}}' -ErrorAction Stop
  $s = Get-PartitionSupportedSize -DriveLetter '{{source}}' -ErrorAction Stop
  [pscustomobject]@{
    SourceResolved=$true
    SourceVolumeLetter='{{source}}'
    FileSystem=[string]$v.FileSystem
    PartitionType=[string]$p.Type
    GptType=[string]$p.GptType
    IsSystemPartition=[bool]$p.IsSystem
    IsActivePartition=[bool]$p.IsActive
    PartitionSizeBytes=[uint64]$p.Size
    SupportedSizeMinBytes=[uint64]$s.SizeMin
    SupportedSizeMaxBytes=[uint64]$s.SizeMax
    DiskNumber=[int]$d.Number
    PartitionStyle=[string]$d.PartitionStyle
    IsDiskOffline=[bool]$d.IsOffline
    IsDiskReadOnly=[bool]$d.IsReadOnly
    IsRemovable=[bool]($d.BusType -in @('USB','SD','MMC'))
    BusType=[string]$d.BusType
    PartitionAlignmentBytes=[uint64]0
    DriveLettersInUse=[string]((Get-Volume | Where-Object { $_.DriveLetter } | ForEach-Object { $_.DriveLetter }) -join '')
  } | ConvertTo-Json -Compress
} catch {
  [pscustomobject]@{ SourceResolved=$false; SourceVolumeLetter='{{source}}'; FileSystem='' } | ConvertTo-Json -Compress
}
""";
    }

    internal static string BuildExecute(
        ResizePlan plan,
        ResizeFeasibility feasibility,
        int expectedDiskNumber)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(feasibility);

        char source = char.ToUpperInvariant(plan.SourceVolumeLetter);
        char target = char.ToUpperInvariant(plan.NewDriveLetter);
        if (source is < 'A' or > 'Z' || target is < 'A' or > 'Z')
        {
            throw new ArgumentException("Resize plans must contain valid drive letters.", nameof(plan));
        }

        ulong devDriveSize = feasibility.AlignedShrinkBytes;
        string label = SanitizeLabel(plan.Label);

        return $$"""
$ErrorActionPreference='Stop'
if (Get-Volume -DriveLetter '{{target}}' -ErrorAction SilentlyContinue) { throw "Drive letter {{target}}: is already in use." }

# Re-query immediately before mutation. The earlier preview/snapshot is advisory; these values are
# authoritative for the shrink that follows.
$currentPartition = Get-Partition -DriveLetter '{{source}}' -ErrorAction Stop
$currentDisk = Get-Disk -Number $currentPartition.DiskNumber -ErrorAction Stop
$currentVolume = Get-Volume -DriveLetter '{{source}}' -ErrorAction Stop
$currentSupported = Get-PartitionSupportedSize -DriveLetter '{{source}}' -ErrorAction Stop
$currentDiskNumber = [int]$currentDisk.Number
$currentFileSystem = [string]$currentVolume.FileSystem
$currentPartitionType = [string]$currentPartition.Type
$currentBusType = [string]$currentDisk.BusType

if ($currentDiskNumber -ne {{expectedDiskNumber}}) { throw "The source volume moved to a different disk. Retry the preview." }
if ([bool]$currentDisk.IsOffline) { throw "The source disk is offline." }
if ([bool]$currentDisk.IsReadOnly) { throw "The source disk is read-only." }
if ($currentBusType -in @('USB','SD','MMC')) { throw "Refusing to repartition a removable disk." }
if ([string]$currentDisk.PartitionStyle -in @('RAW','Unknown','')) { throw "The source disk is not initialized." }
if ([bool]$currentPartition.IsSystem -or $currentPartitionType -in @('Reserved','Recovery','System')) { throw "Refusing to resize a protected partition." }
if ($currentFileSystem -notin @('NTFS','ReFS')) { throw "The source volume is no longer NTFS or ReFS." }

$currentSize = [uint64]$currentPartition.Size
$currentMinimum = [uint64]$currentSupported.SizeMin
if ($currentSize -le $currentMinimum) { throw "The source volume no longer has reclaimable space." }
$currentReclaimable = $currentSize - $currentMinimum
if ($currentReclaimable -lt [uint64]{{devDriveSize}}) { throw "Reclaimable space changed. Retry the preview." }
$newSourceSize = $currentSize - [uint64]{{devDriveSize}}

$mutationMarker = [Console]::Error
$mutationMarker.WriteLine('{{MutationStartedMarker}}')
$mutationMarker.Flush()
Resize-Partition -DriveLetter '{{source}}' -Size $newSourceSize
$null = New-Partition -DiskNumber $currentDiskNumber -Size {{devDriveSize}} -DriveLetter '{{target}}'
Format-Volume -DriveLetter '{{target}}' -DevDrive -FileSystem ReFS -NewFileSystemLabel '{{label}}' -Confirm:$false | Out-Null
$fp = Get-Partition -DriveLetter '{{target}}' -ErrorAction Stop
$fv = Get-Volume -DriveLetter '{{target}}' -ErrorAction Stop
[pscustomobject]@{ FinalDriveLetter=[string]$fp.DriveLetter; FinalPartitionSizeBytes=[uint64]$fp.Size; FinalFileSystem=[string]$fv.FileSystem } | ConvertTo-Json -Compress
""";
    }

    internal static bool MutationMayHaveStarted(int exitCode, string? standardError) =>
        exitCode < 0 ||
        (!string.IsNullOrEmpty(standardError) &&
         standardError.Contains(MutationStartedMarker, StringComparison.Ordinal));

    internal static string RemoveMutationMarker(string? standardError) =>
        string.IsNullOrEmpty(standardError)
            ? string.Empty
            : standardError.Replace(MutationStartedMarker, string.Empty, StringComparison.Ordinal).Trim();

    internal static string SanitizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "DevDrive";
        }

        var builder = new StringBuilder();
        foreach (char c in label.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')
            {
                builder.Append(c);
            }

            if (builder.Length >= 32)
            {
                break;
            }
        }

        return builder.Length == 0 ? "DevDrive" : builder.ToString();
    }
}
