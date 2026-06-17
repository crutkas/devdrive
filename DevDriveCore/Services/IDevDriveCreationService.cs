using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// UI-agnostic orchestration for the Dev Drive creation flow. See
/// <see cref="DevDriveCreationService"/> for the safety contract (resize is simulation-only; the VHDX
/// path is gated behind explicit confirmation and tested against a mock native API).
/// </summary>
public interface IDevDriveCreationService
{
    /// <summary>
    /// Creates and attaches the VHDX described by <paramref name="plan"/> (which must have
    /// <see cref="DevDriveCreationPlan.Source"/> == <see cref="DevDriveCreationSource.Vhdx"/>). Does
    /// NOT format the volume; <see cref="DevDriveCreationResult.FormatPending"/> is set.
    /// </summary>
    Task<DevDriveCreationResult> CreateVhdDevDriveAsync(DevDriveCreationPlan plan, CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes a SAFE preview of the resize-an-existing-volume path. Pure arithmetic — never shrinks,
    /// partitions, or formats anything.
    /// </summary>
    DevDriveResizeSimulation SimulateResize(DevDriveCreationPlan plan);
}
