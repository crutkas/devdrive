namespace DevDriveCore.Models;

/// <summary>
/// Where the space for a new Dev Drive comes from. These mirror the two real platform sources the
/// Windows Settings "Create Dev Drive" wizard offers (see DevDrive-Analysis.md).
/// </summary>
public enum DevDriveCreationSource
{
    /// <summary>Create a new virtual disk (<c>.vhdx</c>) file, attach it, then format it as a Dev Drive.</summary>
    Vhdx = 0,

    /// <summary>Shrink an existing volume and carve a new partition out of the freed space, then format it.</summary>
    ResizeExistingVolume = 1,
}
