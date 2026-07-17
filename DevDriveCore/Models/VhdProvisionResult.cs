namespace DevDriveCore.Models;

/// <summary>
/// Result of a VHDX Dev Drive provisioning run. Pure data shared across the UAC boundary.
/// </summary>
public sealed record VhdProvisionResult
{
    /// <summary>True when the requested provisioning stage completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>True when privileged disk work began and was not confirmed as rolled back.</summary>
    public bool Executed { get; init; }

    /// <summary>True when final disk state could not be established safely.</summary>
    public bool StateUnknown { get; init; }

    /// <summary>True when a failed create was confirmed detached and deleted.</summary>
    public bool RolledBack { get; init; }

    /// <summary>Human-readable result or recovery guidance.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Path of the backing <c>.vhdx</c> file.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Physical device path reported on attach, e.g. <c>\\.\PhysicalDrive7</c>.</summary>
    public string PhysicalPath { get; init; } = string.Empty;

    /// <summary>OS disk number parsed from <see cref="PhysicalPath"/> (e.g. 7), or <c>null</c> when unparseable.</summary>
    public int? DiskNumber { get; init; }

    /// <summary>Actual assigned drive letter, when verified.</summary>
    public char? DriveLetter { get; init; }

    /// <summary>Actual formatted partition size read back from Windows, when verified.</summary>
    public ulong SizeBytes { get; init; }

    /// <summary>Actual filesystem read back from Windows, when verified.</summary>
    public string FileSystem { get; init; } = string.Empty;

    /// <summary><see cref="ReversibilityEntry.Id"/> of the entry recorded for this provisioning.</summary>
    public string ReversibilityId { get; init; } = string.Empty;
}
