namespace DevDriveCore.Models;

/// <summary>
/// Immutable, UI-agnostic description of a fixed volume on the system.
/// </summary>
/// <remarks>
/// Populated by <see cref="DevDriveCore.Services.IDevDriveService.GetVolumes"/>. Every
/// field is computed from public Windows APIs only (Storage WMI v2 +
/// FSCTL_QUERY_PERSISTENT_VOLUME_STATE), so this type can be lifted directly into the
/// Windows Settings handler during backport.
/// </remarks>
public sealed record VolumeInfo
{
    /// <summary>Drive letter without the colon (e.g. <c>'G'</c>), or <c>null</c> for a letterless volume.</summary>
    public char? DriveLetter { get; init; }

    /// <summary>Volume label (e.g. "DevDrive"). May be empty.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>File system as reported by WMI (e.g. "NTFS", "ReFS").</summary>
    public string FileSystemType { get; init; } = string.Empty;

    /// <summary>Total capacity in bytes.</summary>
    public ulong SizeBytes { get; init; }

    /// <summary>Free space in bytes.</summary>
    public ulong FreeBytes { get; init; }

    /// <summary>
    /// False when WMI returned no size for this volume. Distinguishes a volume nobody could
    /// measure from one that genuinely holds nothing, which otherwise both read "0 B free of 0 B".
    /// </summary>
    public bool IsSizeKnown { get; init; } = true;

    /// <summary>True when PERSISTENT_VOLUME_STATE_DEV_VOLUME (0x2000) is set. The must-have signal.</summary>
    public bool IsDevDrive { get; init; }

    /// <summary>
    /// False when the Dev Drive flags could not be read at all — a BitLocker-locked volume, one
    /// that is not ready, or a locked-down machine that denies the handle.
    /// </summary>
    /// <remarks>
    /// The FSCTL returns no flags in those cases, which decoded to "not a Dev Drive" and rendered
    /// a flat <c>No</c>. That states the opposite of the truth about a real, trusted Dev Drive
    /// that simply happened to be locked, and it is the same mistake as printing 0 bytes for a
    /// folder nobody has measured: a failure to read is not a reading of false.
    ///
    /// Defaults to <see langword="true"/> so every construction site that knows what it is
    /// building — tests, fakes, scenarios — is unaffected; only the live probe clears it.
    /// </remarks>
    public bool IsDevDriveStateKnown { get; init; } = true;

    /// <summary>
    /// True when PERSISTENT_VOLUME_STATE_TRUSTED_VOLUME (0x4000) is set. Readable unelevated
    /// via the same FSCTL; only meaningful when <see cref="IsDevDrive"/> is true.
    /// </summary>
    public bool IsTrusted { get; init; }

    /// <summary>True when the backing disk's bus type is Virtual / File Backed Virtual.</summary>
    public bool IsVhd { get; init; }

    /// <summary>Best-effort backing file path for a VHD-backed volume, when discoverable; otherwise <c>null</c>.</summary>
    public string? VhdFilePath { get; init; }

    /// <summary>Used bytes (Size - Free), clamped at zero.</summary>
    public ulong UsedBytes => SizeBytes >= FreeBytes ? SizeBytes - FreeBytes : 0UL;

    /// <summary>Fraction (0..1) of the volume that is used; 0 when size is unknown.</summary>
    public double UsedFraction => SizeBytes == 0 ? 0d : (double)UsedBytes / SizeBytes;
}
