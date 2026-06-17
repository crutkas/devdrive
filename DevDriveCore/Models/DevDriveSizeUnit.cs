namespace DevDriveCore.Models;

/// <summary>
/// Unit the size control displays/accepts. The underlying value is always tracked in bytes; this only
/// affects how the NumberBox and unit dropdown present it (binary GB/MB, 1024-based).
/// </summary>
public enum DevDriveSizeUnit
{
    /// <summary>Gibibytes (1024^3 bytes), labelled "GB" to match Windows.</summary>
    Gigabytes = 0,

    /// <summary>Mebibytes (1024^2 bytes), labelled "MB" to match Windows.</summary>
    Megabytes = 1,
}
