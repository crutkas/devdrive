namespace DevDriveCore.Models;

/// <summary>
/// A READ-ONLY snapshot of the source volume, its partition, and its backing disk &#8212; everything
/// <see cref="DevDriveCore.Services.ResizeGuard"/> needs to decide whether a
/// <see cref="ResizePlan"/> is safe to execute. Pure data with no behaviour.
/// </summary>
/// <remarks>
/// <para>
/// In production the elevated helper builds this from public, read-only Storage queries
/// (<c>Get-Partition</c>, <c>Get-Disk</c>, <c>Get-Volume</c>, <c>Get-PartitionSupportedSize</c>).
/// Because the guard takes the snapshot as plain data, the guard is fully unit-testable WITHOUT a real
/// disk: a test just constructs the snapshot describing the scenario it wants to verify (MSR, EFI,
/// recovery, removable, RAW, offline, read-only, too-small, over-reclaimable, &#8230;).
/// </para>
/// </remarks>
public sealed record DiskLayoutSnapshot
{
    /// <summary>True when the source drive letter resolved to a real partition/disk on this system.</summary>
    public bool SourceResolved { get; init; }

    /// <summary>Letter (without the colon) of the source volume the snapshot describes.</summary>
    public char SourceVolumeLetter { get; init; }

    /// <summary>Source file system as reported by Storage (e.g. <c>NTFS</c>, <c>ReFS</c>, <c>RAW</c>, or empty).</summary>
    public string FileSystem { get; init; } = string.Empty;

    // ---- Source partition ----------------------------------------------------------------------

    /// <summary><c>Get-Partition</c> <c>Type</c> (e.g. <c>Basic</c>, <c>Reserved</c>, <c>Recovery</c>, <c>System</c>).</summary>
    public string PartitionType { get; init; } = string.Empty;

    /// <summary>GPT partition type GUID, when known (used to recognise EFI/MSR/recovery partitions).</summary>
    public string GptType { get; init; } = string.Empty;

    /// <summary>True for the EFI System / OS-loader partition (<c>Get-Partition.IsSystem</c>) &#8212; never touch it.</summary>
    public bool IsSystemPartition { get; init; }

    /// <summary>True for the MBR active partition.</summary>
    public bool IsActivePartition { get; init; }

    /// <summary>Current size of the source partition in bytes.</summary>
    public ulong PartitionSizeBytes { get; init; }

    /// <summary>Smallest size the partition can shrink to (<c>Get-PartitionSupportedSize.SizeMin</c>).</summary>
    public ulong SupportedSizeMinBytes { get; init; }

    /// <summary>Largest supported size (<c>Get-PartitionSupportedSize.SizeMax</c>), roughly the current capacity.</summary>
    public ulong SupportedSizeMaxBytes { get; init; }

    // ---- Disk ----------------------------------------------------------------------------------

    /// <summary>OS disk number that hosts the source partition.</summary>
    public int DiskNumber { get; init; }

    /// <summary>Partition table style (<c>GPT</c>, <c>MBR</c>, or <c>RAW</c> when not initialized).</summary>
    public string PartitionStyle { get; init; } = string.Empty;

    /// <summary>True when the disk is offline &#8212; refuse.</summary>
    public bool IsDiskOffline { get; init; }

    /// <summary>True when the disk is read-only &#8212; refuse.</summary>
    public bool IsDiskReadOnly { get; init; }

    /// <summary>True when the disk is removable (e.g. USB / SD) &#8212; refuse to repartition it.</summary>
    public bool IsRemovable { get; init; }

    /// <summary>Disk bus type (e.g. <c>NVMe</c>, <c>SATA</c>, <c>USB</c>, <c>File Backed Virtual</c>).</summary>
    public string BusType { get; init; } = string.Empty;

    /// <summary>
    /// Disk alignment in bytes the carved partition must round to. <c>0</c> means "use the default"
    /// (<see cref="DevDriveCore.Services.ResizeGuard.DefaultAlignmentBytes"/>, 1&#160;MiB).
    /// </summary>
    public ulong PartitionAlignmentBytes { get; init; }

    /// <summary>
    /// Concatenated drive letters currently mounted on the machine (e.g. <c>"CDG"</c>), captured from a
    /// read-only <c>Get-Volume</c> query. <see cref="DevDriveCore.Services.ResizeGuard"/> refuses a plan
    /// whose new Dev Drive letter already appears here, so the carve can't collide with a mounted volume.
    /// Empty when not captured (older/unpopulated snapshots), which keeps the guard backward-compatible.
    /// </summary>
    public string DriveLettersInUse { get; init; } = string.Empty;
}
