using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;

namespace DevDriveCore.Services;

/// <summary>
/// Default <see cref="IDevDriveService"/>. Composes <see cref="IStorageQuery"/> (volume/disk
/// enumeration), <see cref="INativeVolumeApi"/> (Dev Drive flag detection) and
/// <see cref="IProcessRunner"/> (fsutil trust/filter detail). All decision logic lives here so it
/// can be unit-tested against mocks of those three interfaces.
/// </summary>
public sealed class DevDriveService : IDevDriveService
{
    // MSFT_Volume.DriveType: 3 == Fixed (the only type M1 lists).
    private const uint DriveTypeFixed = 3;

    // MSFT_Disk.BusType values that denote a virtual-disk backing.
    private const ushort BusTypeVirtual = 14;            // "Virtual"
    private const ushort BusTypeFileBackedVirtual = 15;  // "File Backed Virtual" (VHD/VHDX)

    private readonly IStorageQuery _storage;
    private readonly INativeVolumeApi _native;
    private readonly IProcessRunner _processRunner;

    /// <summary>Creates a service over the supplied platform abstractions.</summary>
    public DevDriveService(IStorageQuery storage, INativeVolumeApi native, IProcessRunner processRunner)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    /// <summary>Convenience factory wiring the real platform implementations.</summary>
    public static DevDriveService CreateDefault() =>
        new(new StorageQueryWmi(), new NativeVolumeApi(), new ProcessRunner());

    /// <inheritdoc />
    public IReadOnlyList<VolumeInfo> GetVolumes()
    {
        IReadOnlyList<StorageVolumeRecord> volumes = _storage.GetVolumes();
        IReadOnlyList<StoragePartitionRecord> partitions = _storage.GetPartitions();
        IReadOnlyList<StorageDiskRecord> disks = _storage.GetDisks();

        var diskByNumber = new Dictionary<uint, StorageDiskRecord>();
        foreach (StorageDiskRecord disk in disks)
        {
            diskByNumber[disk.Number] = disk;
        }

        var partitionByLetter = new Dictionary<char, StoragePartitionRecord>();
        foreach (StoragePartitionRecord partition in partitions)
        {
            if (partition.DriveLetter is char letter && !partitionByLetter.ContainsKey(letter))
            {
                partitionByLetter[letter] = partition;
            }
        }

        var result = new List<VolumeInfo>();
        foreach (StorageVolumeRecord volume in volumes)
        {
            // M1 lists fixed volumes only.
            if (volume.DriveType != DriveTypeFixed)
            {
                continue;
            }

            (bool isDevDrive, bool isTrusted) = DetectDevDrive(volume.DriveLetter);
            (bool isVhd, string? vhdPath) = DetectVhd(volume.DriveLetter, partitionByLetter, diskByNumber);

            result.Add(new VolumeInfo
            {
                DriveLetter = volume.DriveLetter,
                Label = volume.Label,
                FileSystemType = volume.FileSystem,
                SizeBytes = volume.SizeBytes,
                FreeBytes = volume.FreeBytes,
                IsDevDrive = isDevDrive,
                IsTrusted = isTrusted,
                IsVhd = isVhd,
                VhdFilePath = vhdPath,
            });
        }

        // Lettered volumes first (alphabetical), then letterless (e.g. Recovery) last.
        return result
            .OrderBy(v => v.DriveLetter.HasValue ? 0 : 1)
            .ThenBy(v => v.DriveLetter ?? char.MaxValue)
            .ToList();
    }

    /// <inheritdoc />
    public DevDriveTrustInfo? GetDevDriveTrustInfo(char driveLetter)
    {
        char letter = char.ToUpperInvariant(driveLetter);
        ProcessRunResult run = _processRunner.Run("fsutil", $"devdrv query {letter}:");
        return FsutilDevDrvParser.Parse(run.ExitCode, run.StandardOutput, run.StandardError);
    }

    /// <inheritdoc />
    public bool? GetDefenderPerformanceMode()
    {
        try
        {
            // [int] forces the PerformanceModeStatus enum to print as 0/1 regardless of formatting.
            ProcessRunResult run = _processRunner.Run(
                "powershell",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"[int](Get-MpPreference).PerformanceModeStatus\"");

            if (run is null || run.ExitCode < 0 || run.TimedOut)
            {
                return null;
            }

            return PreflightProbe.ParsePerformanceMode(run.StandardOutput);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes the raw FSCTL flags into (IsDevDrive, IsTrusted). Public so it can be exercised
    /// directly and reused by the backport. Returns (false, false) when the volume has no letter
    /// or the flags couldn't be read.
    /// </summary>
    public static (bool IsDevDrive, bool IsTrusted) DecodeFlags(uint? volumeFlags)
    {
        if (volumeFlags is not uint flags)
        {
            return (false, false);
        }

        bool isDev = (flags & PersistentVolumeState.DevVolume) != 0;
        bool isTrusted = (flags & PersistentVolumeState.TrustedVolume) != 0;
        return (isDev, isTrusted);
    }

    private (bool IsDevDrive, bool IsTrusted) DetectDevDrive(char? driveLetter)
    {
        if (driveLetter is not char letter)
        {
            return (false, false);
        }

        uint? flags = _native.QueryPersistentVolumeState($"{letter}:\\");
        return DecodeFlags(flags);
    }

    private (bool IsVhd, string? VhdFilePath) DetectVhd(
        char? driveLetter,
        IReadOnlyDictionary<char, StoragePartitionRecord> partitionByLetter,
        IReadOnlyDictionary<uint, StorageDiskRecord> diskByNumber)
    {
        if (driveLetter is not char letter
            || !partitionByLetter.TryGetValue(letter, out StoragePartitionRecord? partition)
            || !diskByNumber.TryGetValue(partition.DiskNumber, out StorageDiskRecord? disk))
        {
            return (false, null);
        }

        bool isVhd = disk.BusType is BusTypeVirtual or BusTypeFileBackedVirtual;
        string? vhdPath = isVhd ? NormalizeVhdPath(disk.Location) : null;
        return (isVhd, vhdPath);
    }

    /// <summary>
    /// MSFT_Disk.Location for a file-backed virtual disk is typically the backing file path; for a
    /// physical disk it is a topology string ("Integrated : Bus 0 ..."). Returns it only when it
    /// looks like a path. (M2/M3: MSFT_DiskImage.ImagePath is the precise source.)
    /// Public so it can be exercised directly and reused by the backport.
    /// </summary>
    public static string? NormalizeVhdPath(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        location = location.Trim();
        bool looksLikePath = location.Contains(@":\", StringComparison.Ordinal)
            || location.StartsWith(@"\\", StringComparison.Ordinal);
        return looksLikePath ? location : null;
    }
}
