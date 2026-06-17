namespace DevDriveCore.Abstractions;

/// <summary>
/// A physical disk as reported by Storage WMI v2 (<c>MSFT_PhysicalDisk</c>), flattened into a plain
/// record. Used to describe the storage class (media + bus) of the disk backing the Dev Drive.
/// </summary>
public sealed record PhysicalDiskRecord
{
    /// <summary>
    /// <c>DeviceId</c> as an index. <c>MSFT_PhysicalDisk.DeviceId</c> is a <em>string</em> in WMI; the
    /// query parses it to an integer so it can be matched against <see cref="StoragePartitionRecord.DiskNumber"/>.
    /// </summary>
    public uint DeviceId { get; init; }

    /// <summary>
    /// <c>MediaType</c> enum: 0 == Unspecified, 3 == HDD, 4 == SSD, 5 == SCM (storage-class memory).
    /// </summary>
    public ushort MediaType { get; init; }

    /// <summary>
    /// <c>BusType</c> enum: 17 == NVMe, 11 == SATA, 3 == ATA, 10 == SAS, 7 == USB, 8 == RAID,
    /// 9 == iSCSI, 14 == Virtual, 15 == File-backed virtual, 16 == Storage Spaces.
    /// </summary>
    public ushort BusType { get; init; }

    /// <summary><c>FriendlyName</c>, e.g. "Samsung SSD 990 PRO 2TB" or "Msft Virtual Disk".</summary>
    public string? FriendlyName { get; init; }
}

/// <summary>
/// Wraps the <c>MSFT_PhysicalDisk</c> Storage WMI v2 query. Returning a plain record (not a
/// <c>ManagementObject</c>) keeps the composing pre-flight probe free of <c>System.Management</c> and
/// fully mockable.
/// </summary>
public interface IPhysicalDiskQuery
{
    /// <summary>Enumerates <c>MSFT_PhysicalDisk</c> (media type + bus type per disk).</summary>
    IReadOnlyList<PhysicalDiskRecord> GetPhysicalDisks();
}
