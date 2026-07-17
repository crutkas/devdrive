using DevDriveCore.Services;

namespace DevDriveCore.Models;

/// <summary>
/// Immutable, UI-agnostic description of the Dev Drive the user configured in the creation flow. A
/// SAFE description only — building one changes nothing on the machine. The (gated, confirmed)
/// orchestration in <see cref="DevDriveCore.Services.DevDriveCreationService"/> turns a
/// <see cref="DevDriveCreationSource.Vhdx"/> plan into a real <see cref="VhdProvisionPlan"/>; a
/// <see cref="DevDriveCreationSource.ResizeExistingVolume"/> plan is projected onto
/// <see cref="ResizePlan"/> and handled by <see cref="DevDriveCore.Abstractions.IVolumeResizer"/>.
/// </summary>
public sealed record DevDriveCreationPlan
{
    /// <summary>Where the space comes from.</summary>
    public DevDriveCreationSource Source { get; init; }

    /// <summary>Volume label shown in File Explorer / Settings.</summary>
    public string Label { get; init; } = "DevDrive";

    /// <summary>Drive letter to assign (without the colon), e.g. <c>'D'</c>.</summary>
    public char DriveLetter { get; init; } = 'D';

    /// <summary>Chosen Dev Drive size in bytes (already clamped to the 50 GiB minimum / source maximum).</summary>
    public ulong SizeBytes { get; init; }

    // ---- VHDX-specific --------------------------------------------------------------------------

    /// <summary>Full path of the backing <c>.vhdx</c> to create (VHDX source only).</summary>
    public string VhdFilePath { get; init; } = string.Empty;

    /// <summary>When <c>true</c> the VHDX grows on demand (dynamic); otherwise it is pre-allocated (fixed).</summary>
    public bool VhdIsDynamic { get; init; } = true;

    // ---- Resize-source snapshot (resize source only) --------------------------------------------

    /// <summary>Letter of the volume being shrunk (resize source only).</summary>
    public char? SourceVolumeLetter { get; init; }

    /// <summary>Total capacity of the source volume in bytes.</summary>
    public ulong SourceVolumeSizeBytes { get; init; }

    /// <summary>Used bytes on the source volume.</summary>
    public ulong SourceVolumeUsedBytes { get; init; }

    /// <summary>Free (approximate shrinkable) bytes on the source volume.</summary>
    public ulong SourceVolumeFreeBytes { get; init; }

    /// <summary>Projects this plan onto the lower-level <see cref="VhdProvisionPlan"/> the provisioner consumes.</summary>
    public VhdProvisionPlan ToVhdProvisionPlan() => new()
    {
        FilePath = VhdFilePath,
        MaximumSizeBytes = checked(SizeBytes + DevDriveSizeMath.VhdContainerHeadroomBytesExact),
        VolumeSizeBytes = SizeBytes,
        DynamicallyExpanding = VhdIsDynamic,
        DriveLetter = char.ToUpperInvariant(DriveLetter),
        Label = string.IsNullOrWhiteSpace(Label) ? "DevDrive" : Label.Trim(),
    };
}
