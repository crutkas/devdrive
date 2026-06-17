namespace DevDriveCore.Services;

/// <summary>
/// Formats a disk's <c>MSFT_PhysicalDisk</c> media type + bus type (and friendly name) into a short,
/// human-readable storage class such as "Samsung SSD 990 PRO · SSD · NVMe" or "Msft Virtual Disk ·
/// Unspecified · SAS". Pure and deterministic, so it is fully unit-testable.
/// </summary>
public static class StorageClassFormatter
{
    /// <summary>
    /// Combines the friendly name, media-type label, and bus-type label (skipping blank/unknown
    /// parts) with " · ". Returns "Unknown" when nothing usable is available.
    /// </summary>
    public static string Format(ushort mediaType, ushort busType, string? friendlyName)
    {
        var parts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(friendlyName))
        {
            parts.Add(friendlyName.Trim());
        }

        parts.Add(MediaTypeLabel(mediaType));

        string? bus = BusTypeLabel(busType);
        if (bus is not null)
        {
            parts.Add(bus);
        }

        return parts.Count == 0 ? "Unknown" : string.Join(" · ", parts);
    }

    /// <summary>Maps a <c>MSFT_PhysicalDisk.MediaType</c> value to a label (never null; "Unspecified" for 0).</summary>
    public static string MediaTypeLabel(ushort mediaType) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM (persistent memory)",
        _ => "Unspecified",
    };

    /// <summary>Maps a <c>MSFT_PhysicalDisk.BusType</c> value to a label, or <c>null</c> when unknown.</summary>
    public static string? BusTypeLabel(ushort busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => null,
    };
}
