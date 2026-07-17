using System.Text.Json;
using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Builds the exact Storage-cmdlet sequence that turns a newly attached RAW VHDX into a Dev Drive.
/// </summary>
internal static class VhdPowerShellScript
{
    internal const string MutationStartedMarker = "__DDM_VHD_MUTATION_STARTED__";
    internal const int MinimumDevDriveBuild = 22621;
    internal const int MinimumDevDriveRevision = 2338;

    internal static string BuildCapabilityProbe() => $$"""
$ErrorActionPreference='Stop'
$storageModule=[System.IO.Path]::Combine([Environment]::SystemDirectory,'WindowsPowerShell','v1.0','Modules','Storage','Storage.psd1')
Import-Module -Name $storageModule -Force -ErrorAction Stop
$osVersion=[Environment]::OSVersion.Version
$updateBuildRevision=try { [int][Microsoft.Win32.Registry]::GetValue('HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion','UBR',0) } catch { 0 }
$formatVolumeCommand=Microsoft.PowerShell.Core\Get-Command -Name 'Storage\Format-Volume' -ErrorAction SilentlyContinue
$supported=[bool](
  (($osVersion.Build -gt {{MinimumDevDriveBuild}}) -or ($osVersion.Build -eq {{MinimumDevDriveBuild}} -and $updateBuildRevision -ge {{MinimumDevDriveRevision}})) -and
  ($null -ne $formatVolumeCommand) -and
  $formatVolumeCommand.Parameters.ContainsKey('DevDrive'))
[pscustomobject]@{
  Supported=$supported
  Build=[int]$osVersion.Build
  Revision=$updateBuildRevision
  HasDevDriveParameter=[bool](($null -ne $formatVolumeCommand) -and $formatVolumeCommand.Parameters.ContainsKey('DevDrive'))
} | ConvertTo-Json -Compress
""";

    internal static string BuildFinalize(VhdProvisionPlan plan, int expectedDiskNumber)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (expectedDiskNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedDiskNumber));
        }

        char target = char.ToUpperInvariant(plan.DriveLetter);
        if (target is < 'D' or > 'Z')
        {
            throw new ArgumentException("VHDX Dev Drives require a drive letter from D through Z.", nameof(plan));
        }

        if (plan.VolumeSizeBytes < DevDriveSizeMath.MinimumSizeBytesExact)
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "A Dev Drive must be at least 50 GiB.");
        }

        if (plan.MaximumSizeBytes <= plan.VolumeSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "The VHD container must include partition metadata headroom.");
        }

        string imagePath = EscapePowerShellLiteral(Path.GetFullPath(plan.FilePath));
        string label = EscapePowerShellLiteral(ResizePowerShellScript.SanitizeLabel(plan.Label));

        return $$"""
$ErrorActionPreference='Stop'
$storageModule=[System.IO.Path]::Combine([Environment]::SystemDirectory,'WindowsPowerShell','v1.0','Modules','Storage','Storage.psd1')
Import-Module -Name $storageModule -Force -ErrorAction Stop
$osVersion=[Environment]::OSVersion.Version
$updateBuildRevision=try { [int][Microsoft.Win32.Registry]::GetValue('HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion','UBR',0) } catch { 0 }
$formatVolumeCommand=Microsoft.PowerShell.Core\Get-Command -Name 'Storage\Format-Volume' -ErrorAction SilentlyContinue
if (-not (
  (($osVersion.Build -gt {{MinimumDevDriveBuild}}) -or ($osVersion.Build -eq {{MinimumDevDriveBuild}} -and $updateBuildRevision -ge {{MinimumDevDriveRevision}})) -and
  ($null -ne $formatVolumeCommand) -and
  $formatVolumeCommand.Parameters.ContainsKey('DevDrive'))) {
  throw "This Windows build or inbox Storage module does not support Dev Drive formatting."
}
$imagePath='{{imagePath}}'
$target='{{target}}'
$expectedDiskNumber={{expectedDiskNumber}}
$expectedSize=[uint64]{{plan.MaximumSizeBytes}}
$volumeSize=[uint64]{{plan.VolumeSizeBytes}}

if (Storage\Get-Volume -DriveLetter $target -ErrorAction SilentlyContinue) { throw "Drive letter $target`: is already in use." }
$image = Storage\Get-DiskImage -ImagePath $imagePath -ErrorAction Stop
if (-not [bool]$image.Attached) { throw "The requested VHDX is no longer attached." }
$imageDisks = @($image | Storage\Get-Disk -ErrorAction Stop)
if ($imageDisks.Count -ne 1) { throw "The requested VHDX did not resolve to exactly one disk." }
$disk = $imageDisks[0]
if ([int]$disk.Number -ne $expectedDiskNumber) { throw "The VHDX disk identity changed. Refusing to continue." }
if ([bool]$disk.IsOffline) { throw "The VHDX disk is offline." }
if ([bool]$disk.IsReadOnly) { throw "The VHDX disk is read-only." }
if ([string]$disk.PartitionStyle -ne 'RAW') { throw "The VHDX is no longer RAW. Refusing to initialize or format it." }
$partitions = @(Storage\Get-Partition -DiskNumber $expectedDiskNumber -ErrorAction SilentlyContinue)
if ($partitions.Count -ne 0) { throw "The VHDX already contains a partition. Refusing to format it." }
$actualSize=[uint64]$disk.Size
$sizeDelta = if ($actualSize -gt $expectedSize) { $actualSize - $expectedSize } else { $expectedSize - $actualSize }
if ($sizeDelta -gt [uint64]1048576) { throw "The VHDX size does not match the authorized plan." }
if (Storage\Get-Volume -DriveLetter $target -ErrorAction SilentlyContinue) { throw "Drive letter $target`: became unavailable." }

$mutationMarker = [Console]::Error
$mutationMarker.WriteLine('{{MutationStartedMarker}}')
$mutationMarker.Flush()
$null = Storage\Initialize-Disk -Number $expectedDiskNumber -PartitionStyle GPT -PassThru
$null = Storage\New-Partition -DiskNumber $expectedDiskNumber -Size $volumeSize -DriveLetter $target
Storage\Format-Volume -DriveLetter $target -DevDrive -FileSystem ReFS -NewFileSystemLabel '{{label}}' -Confirm:$false | Out-Null

$finalImage = Storage\Get-DiskImage -ImagePath $imagePath -ErrorAction Stop
$finalDisk = $finalImage | Storage\Get-Disk -ErrorAction Stop
$finalPartition = Storage\Get-Partition -DriveLetter $target -ErrorAction Stop
$finalVolume = Storage\Get-Volume -DriveLetter $target -ErrorAction Stop
if ([int]$finalDisk.Number -ne $expectedDiskNumber -or [int]$finalPartition.DiskNumber -ne $expectedDiskNumber) {
  throw "The formatted volume is not on the authorized VHDX."
}
$finalPartitionSize=[uint64]$finalPartition.Size
$partitionSizeDelta = if ($finalPartitionSize -gt $volumeSize) { $finalPartitionSize - $volumeSize } else { $volumeSize - $finalPartitionSize }
if ($partitionSizeDelta -gt [uint64]1048576) { throw "The final partition size does not match the authorized plan." }
if ([string]$finalVolume.FileSystem -ne 'ReFS') { throw "The final volume is not ReFS." }
$fsutil=[System.IO.Path]::Combine([Environment]::SystemDirectory,'fsutil.exe')
$devDriveQuery = & $fsutil devdrv query "$target`:" 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "Windows did not verify the formatted volume as a Dev Drive: $devDriveQuery" }
[pscustomobject]@{
  DriveLetter=[string]$finalPartition.DriveLetter
  SizeBytes=$finalPartitionSize
  FileSystem=[string]$finalVolume.FileSystem
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

    internal static bool TryParseFinalState(string? json, out VhdFinalState state)
    {
        state = new VhdFinalState();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            VhdFinalState? parsed = JsonSerializer.Deserialize<VhdFinalState>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is null ||
                parsed.DriveLetter is < 'D' or > 'Z' ||
                parsed.SizeBytes == 0 ||
                !parsed.FileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase) ||
                !parsed.IsDevDrive)
            {
                return false;
            }

            state = parsed with { DriveLetter = char.ToUpperInvariant(parsed.DriveLetter) };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseCapability(string? json, out VhdCapabilityState state)
    {
        state = new VhdCapabilityState();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            VhdCapabilityState? parsed = JsonSerializer.Deserialize<VhdCapabilityState>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is null || parsed.Build <= 0 || parsed.Revision < 0)
            {
                return false;
            }

            state = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}

internal sealed record VhdCapabilityState
{
    public bool Supported { get; init; }

    public int Build { get; init; }

    public int Revision { get; init; }

    public bool HasDevDriveParameter { get; init; }
}

internal sealed record VhdFinalState
{
    public char DriveLetter { get; init; }

    public ulong SizeBytes { get; init; }

    public string FileSystem { get; init; } = string.Empty;

    public bool IsDevDrive { get; init; }
}
