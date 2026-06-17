namespace DevDriveCore.Abstractions;

/// <summary>A volume as reported by Storage WMI v2 (<c>MSFT_Volume</c>), flattened into a plain record.</summary>
public sealed record StorageVolumeRecord
{
    /// <summary>Drive letter without colon, or <c>null</c> when the volume has none.</summary>
    public char? DriveLetter { get; init; }

    /// <summary><c>FileSystemLabel</c>.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary><c>FileSystem</c> string (e.g. "NTFS", "ReFS").</summary>
    public string FileSystem { get; init; } = string.Empty;

    /// <summary><c>Size</c> in bytes.</summary>
    public ulong SizeBytes { get; init; }

    /// <summary><c>SizeRemaining</c> in bytes.</summary>
    public ulong FreeBytes { get; init; }

    /// <summary><c>DriveType</c> enum: 3 == Fixed (the only type M1 lists).</summary>
    public uint DriveType { get; init; }

    /// <summary><c>Path</c>, e.g. <c>\\?\Volume{guid}\</c>.</summary>
    public string? Path { get; init; }
}

/// <summary>A partition as reported by Storage WMI v2 (<c>MSFT_Partition</c>).</summary>
public sealed record StoragePartitionRecord
{
    /// <summary>Drive letter without colon, or <c>null</c>.</summary>
    public char? DriveLetter { get; init; }

    /// <summary>Owning physical disk index (<c>DiskNumber</c>).</summary>
    public uint DiskNumber { get; init; }
}

/// <summary>A physical/virtual disk as reported by Storage WMI v2 (<c>MSFT_Disk</c>).</summary>
public sealed record StorageDiskRecord
{
    /// <summary>Disk index (<c>Number</c>), matched against <see cref="StoragePartitionRecord.DiskNumber"/>.</summary>
    public uint Number { get; init; }

    /// <summary>
    /// <c>BusType</c> enum. 14 == Virtual, 15 == "File Backed Virtual" — the VHD/VHDX signals.
    /// </summary>
    public ushort BusType { get; init; }

    /// <summary>
    /// <c>Location</c>. For file-backed virtual disks this is typically the backing file path; for
    /// physical disks it's a topology string.
    /// </summary>
    public string? Location { get; init; }

    /// <summary><c>Model</c>.</summary>
    public string? Model { get; init; }

    /// <summary><c>FriendlyName</c>.</summary>
    public string? FriendlyName { get; init; }
}

/// <summary>
/// Wraps Storage WMI v2 (<c>root\Microsoft\Windows\Storage</c>) queries. Returning plain records
/// (not <c>ManagementObject</c>s) keeps the composing service free of <c>System.Management</c> and
/// fully mockable.
/// </summary>
public interface IStorageQuery
{
    /// <summary>Enumerates <c>MSFT_Volume</c>.</summary>
    IReadOnlyList<StorageVolumeRecord> GetVolumes();

    /// <summary>Enumerates <c>MSFT_Partition</c> (used to map a volume to its disk).</summary>
    IReadOnlyList<StoragePartitionRecord> GetPartitions();

    /// <summary>Enumerates <c>MSFT_Disk</c> (used to detect VHD-backed volumes via bus type).</summary>
    IReadOnlyList<StorageDiskRecord> GetDisks();
}
