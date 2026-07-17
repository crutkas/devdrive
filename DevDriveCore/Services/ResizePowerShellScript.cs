using System.Text;
using System.Text.Json;
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
$storageModule=[System.IO.Path]::Combine([Environment]::SystemDirectory,'WindowsPowerShell','v1.0','Modules','Storage','Storage.psd1')
Import-Module -Name $storageModule -Force -ErrorAction Stop
$osVersion=[Environment]::OSVersion.Version
$updateBuildRevision=try { [int][Microsoft.Win32.Registry]::GetValue('HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion','UBR',0) } catch { 0 }
$formatVolumeCommand=Microsoft.PowerShell.Core\Get-Command -Name 'Storage\Format-Volume' -ErrorAction SilentlyContinue
$supportsDevDriveFormat=[bool](
  (($osVersion.Build -gt 22621) -or ($osVersion.Build -eq 22621 -and $updateBuildRevision -ge 2338)) -and
  ($null -ne $formatVolumeCommand) -and
  $formatVolumeCommand.Parameters.ContainsKey('DevDrive'))
try {
  $p = Storage\Get-Partition -DriveLetter '{{source}}' -ErrorAction Stop
  $d = Storage\Get-Disk -Number $p.DiskNumber -ErrorAction Stop
  $v = Storage\Get-Volume -DriveLetter '{{source}}' -ErrorAction Stop
  $s = Storage\Get-PartitionSupportedSize -DriveLetter '{{source}}' -ErrorAction Stop
  [pscustomobject]@{
    SourceResolved=$true
    SourceVolumeLetter='{{source}}'
    FileSystem=[string]$v.FileSystem
    PartitionType=[string]$p.Type
    GptType=[string]$p.GptType
    IsSystemPartition=[bool]$p.IsSystem
    IsActivePartition=[bool]$p.IsActive
    PartitionSizeBytes=[uint64]$p.Size
    PartitionNumber=[int]$p.PartitionNumber
    PartitionOffsetBytes=[uint64]$p.Offset
    PartitionGuid=[string]$p.Guid
    SupportedSizeMinBytes=[uint64]$s.SizeMin
    SupportedSizeMaxBytes=[uint64]$s.SizeMax
    DiskNumber=[int]$d.Number
    DiskUniqueId=[string]$d.UniqueId
    PartitionStyle=[string]$d.PartitionStyle
    IsDiskOffline=[bool]$d.IsOffline
    IsDiskReadOnly=[bool]$d.IsReadOnly
    IsRemovable=[bool]($d.BusType -in @('USB','SD','MMC'))
    BusType=[string]$d.BusType
    PartitionAlignmentBytes=[uint64]0
    DriveLettersInUse=[string]((Storage\Get-Volume | Where-Object { $_.DriveLetter } | ForEach-Object { $_.DriveLetter }) -join '')
    SupportsDevDriveFormat=$supportsDevDriveFormat
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
        if (!ResizeGuard.HasExpectedPreviewIdentity(plan))
        {
            throw new ArgumentException("Resize execution requires a complete verified live identity.", nameof(plan));
        }

        string expectedDiskUniqueId = EscapePowerShellLiteral(plan.ExpectedDiskUniqueId);
        string expectedPartitionGuid = EscapePowerShellLiteral(plan.ExpectedPartitionGuid);

        return $$"""
$ErrorActionPreference='Stop'
$storageModule=[System.IO.Path]::Combine([Environment]::SystemDirectory,'WindowsPowerShell','v1.0','Modules','Storage','Storage.psd1')
Import-Module -Name $storageModule -Force -ErrorAction Stop
$osVersion=[Environment]::OSVersion.Version
$updateBuildRevision=try { [int][Microsoft.Win32.Registry]::GetValue('HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion','UBR',0) } catch { 0 }
$formatVolumeCommand=Microsoft.PowerShell.Core\Get-Command -Name 'Storage\Format-Volume' -ErrorAction SilentlyContinue
if (-not (
  (($osVersion.Build -gt 22621) -or ($osVersion.Build -eq 22621 -and $updateBuildRevision -ge 2338)) -and
  ($null -ne $formatVolumeCommand) -and
  $formatVolumeCommand.Parameters.ContainsKey('DevDrive'))) {
  throw "This Windows build or inbox Storage module does not support Dev Drive formatting."
}
if (Storage\Get-Volume -DriveLetter '{{target}}' -ErrorAction SilentlyContinue) { throw "Drive letter {{target}}: is already in use." }

# Re-query immediately before mutation. The earlier preview/snapshot is advisory; these values are
# authoritative for the shrink that follows.
$currentPartition = Storage\Get-Partition -DriveLetter '{{source}}' -ErrorAction Stop
$currentDisk = Storage\Get-Disk -Number $currentPartition.DiskNumber -ErrorAction Stop
$currentVolume = Storage\Get-Volume -Partition $currentPartition -ErrorAction Stop
$currentSupported = Storage\Get-PartitionSupportedSize -InputObject $currentPartition -ErrorAction Stop
$currentDiskNumber = [int]$currentDisk.Number
$currentFileSystem = [string]$currentVolume.FileSystem
$currentPartitionType = [string]$currentPartition.Type
$currentBusType = [string]$currentDisk.BusType
$currentDiskUniqueId = [string]$currentDisk.UniqueId
$currentPartitionNumber = [int]$currentPartition.PartitionNumber
$currentPartitionOffset = [uint64]$currentPartition.Offset
$currentPartitionGuid = [string]$currentPartition.Guid

if ($currentDiskNumber -ne {{expectedDiskNumber}}) { throw "The source volume moved to a different disk. Try again." }
if (-not [string]::Equals($currentDiskUniqueId.Trim(), '{{expectedDiskUniqueId}}'.Trim(), [StringComparison]::OrdinalIgnoreCase)) { throw "The source disk identity changed. Try again." }
if ($currentPartitionNumber -ne {{plan.ExpectedPartitionNumber!.Value}}) { throw "The source partition number changed. Try again." }
if ($currentPartitionOffset -ne [uint64]{{plan.ExpectedPartitionOffsetBytes!.Value}}) { throw "The source partition offset changed. Try again." }
if ('{{expectedPartitionGuid}}' -and -not [string]::Equals($currentPartitionGuid.Trim(), '{{expectedPartitionGuid}}'.Trim(), [StringComparison]::OrdinalIgnoreCase)) { throw "The source partition identity changed. Try again." }
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
if ($currentReclaimable -lt [uint64]{{devDriveSize}}) { throw "Reclaimable space changed. Try again." }
$newSourceSize = $currentSize - [uint64]{{devDriveSize}}

$mutationMarker = [Console]::Error
$mutationMarker.WriteLine('{{MutationStartedMarker}}')
$mutationMarker.Flush()
Storage\Resize-Partition -InputObject $currentPartition -Size $newSourceSize
$newPartition = Storage\New-Partition -InputObject $currentDisk -Size {{devDriveSize}} -DriveLetter '{{target}}'
$newPartitionUniqueId = [string]$newPartition.UniqueId
if ([string]::IsNullOrWhiteSpace($newPartitionUniqueId)) { throw "Windows did not return a stable identity for the new partition." }
Storage\Format-Volume -Partition $newPartition -DevDrive -FileSystem ReFS -NewFileSystemLabel '{{label}}' -Confirm:$false | Out-Null
$fp = Storage\Get-Partition -UniqueId $newPartitionUniqueId -ErrorAction Stop
$fv = Storage\Get-Volume -Partition $fp -ErrorAction Stop
$finalSize=[uint64]$fp.Size
$finalSizeDelta=if ($finalSize -gt [uint64]{{devDriveSize}}) { $finalSize - [uint64]{{devDriveSize}} } else { [uint64]{{devDriveSize}} - $finalSize }
if ([int]$fp.DiskNumber -ne $currentDiskNumber) { throw "The new partition is not on the verified disk." }
if ([int]$fp.PartitionNumber -ne [int]$newPartition.PartitionNumber) { throw "The new partition identity changed." }
if ([uint64]$fp.Offset -ne [uint64]$newPartition.Offset) { throw "The new partition offset changed." }
if ([string]$fp.DriveLetter -ne '{{target}}') { throw "The new partition has an unexpected drive letter." }
if ($finalSizeDelta -gt [uint64]1048576) { throw "The new partition size does not match the verified size." }
if ([string]$fv.FileSystem -ne 'ReFS') { throw "The new volume is not ReFS." }
$fsutil=[System.IO.Path]::Combine([Environment]::SystemDirectory,'fsutil.exe')
$devDriveQuery=& $fsutil devdrv query '{{target}}:' 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "Windows did not verify the new volume as a Dev Drive: $devDriveQuery" }
[pscustomobject]@{
  FinalDriveLetter=[string]$fp.DriveLetter
  FinalPartitionSizeBytes=$finalSize
  FinalFileSystem=[string]$fv.FileSystem
  FinalDiskNumber=[int]$fp.DiskNumber
  FinalPartitionNumber=[int]$fp.PartitionNumber
  IsDevDrive=$true
} | ConvertTo-Json -Compress
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

    internal static bool TryParseFinalState(
        string? json,
        ResizePlan plan,
        out ResizeFinalState state)
    {
        ArgumentNullException.ThrowIfNull(plan);
        state = new ResizeFinalState();
        if (string.IsNullOrWhiteSpace(json) || !ResizeGuard.HasExpectedPreviewIdentity(plan))
        {
            return false;
        }

        try
        {
            ResizeFinalState? parsed = JsonSerializer.Deserialize<ResizeFinalState>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is null ||
                char.ToUpperInvariant(parsed.FinalDriveLetter) != char.ToUpperInvariant(plan.NewDriveLetter) ||
                parsed.FinalDiskNumber != plan.ExpectedDiskNumber ||
                parsed.FinalPartitionNumber <= 0 ||
                parsed.FinalPartitionSizeBytes == 0 ||
                !parsed.FinalFileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase) ||
                !parsed.IsDevDrive)
            {
                return false;
            }

            ulong expectedSize = plan.ExpectedAlignedShrinkBytes!.Value;
            ulong sizeDelta = parsed.FinalPartitionSizeBytes > expectedSize
                ? parsed.FinalPartitionSizeBytes - expectedSize
                : expectedSize - parsed.FinalPartitionSizeBytes;
            if (sizeDelta > 1024UL * 1024UL)
            {
                return false;
            }

            state = parsed with { FinalDriveLetter = char.ToUpperInvariant(parsed.FinalDriveLetter) };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string EscapePowerShellLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}

internal sealed record ResizeFinalState
{
    public char FinalDriveLetter { get; init; }

    public ulong FinalPartitionSizeBytes { get; init; }

    public string FinalFileSystem { get; init; } = string.Empty;

    public int FinalDiskNumber { get; init; }

    public int FinalPartitionNumber { get; init; }

    public bool IsDevDrive { get; init; }
}
