namespace DevDriveCore.Models;

/// <summary>
/// Computed preview of what shrinking an existing volume to carve out a Dev Drive <em>would</em> do.
/// </summary>
/// <remarks>
/// <b>SAFETY:</b> this is pure arithmetic produced by
/// <see cref="DevDriveCore.Services.DevDriveCreationService.SimulateResize"/>. The resize path is
/// <see cref="IsSimulationOnly"/> by design: no shrink, partition, or format API is ever called. The
/// real implementation would go through the elevated storage broker (<c>Resize-Partition</c> shrink +
/// <c>New-Partition</c> + <c>Format-Volume -DevDrive</c>); that is intentionally out of scope here.
/// </remarks>
public sealed record DevDriveResizeSimulation
{
    /// <summary>Letter of the volume that would be shrunk.</summary>
    public char SourceVolumeLetter { get; init; }

    /// <summary>Letter the new Dev Drive would receive.</summary>
    public char DevDriveLetter { get; init; }

    /// <summary>How many bytes the source would be shrunk by (equals the Dev Drive size).</summary>
    public ulong ShrinkBytes { get; init; }

    /// <summary>Size of the Dev Drive that would be created.</summary>
    public ulong DevDriveBytes { get; init; }

    /// <summary>Source volume's capacity after the shrink.</summary>
    public ulong NewSourceSizeBytes { get; init; }

    /// <summary>Source volume's free space after the shrink.</summary>
    public ulong NewSourceFreeBytes { get; init; }

    /// <summary>Ordered, human-readable steps the real (elevated) operation would perform.</summary>
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    /// <summary>Always <c>true</c>: this build never executes a resize, it only computes this preview.</summary>
    public bool IsSimulationOnly { get; init; } = true;
}
