namespace DevDriveCore.Models;

/// <summary>
/// Result of a <see cref="DevDriveCore.Services.VhdProvisioner"/> run: the created/surfaced disk and
/// the OS disk number a caller would next partition/format. Pure data.
/// </summary>
public sealed record VhdProvisionResult
{
    /// <summary>True when the disk was created and surfaced.</summary>
    public bool Success { get; init; }

    /// <summary>Path of the backing <c>.vhdx</c> file.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Physical device path reported on attach, e.g. <c>\\.\PhysicalDrive7</c>.</summary>
    public string PhysicalPath { get; init; } = string.Empty;

    /// <summary>OS disk number parsed from <see cref="PhysicalPath"/> (e.g. 7), or <c>null</c> when unparseable.</summary>
    public int? DiskNumber { get; init; }

    /// <summary><see cref="ReversibilityEntry.Id"/> of the entry recorded for this provisioning.</summary>
    public string ReversibilityId { get; init; } = string.Empty;
}
