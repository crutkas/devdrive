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
    /// Creates, attaches, initializes, partitions, and formats the VHDX described by
    /// <paramref name="plan"/> as a ReFS Dev Drive.
    /// </summary>
    Task<DevDriveCreationResult> CreateVhdDevDriveAsync(DevDriveCreationPlan plan, CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes a SAFE preview of the resize-an-existing-volume path. Pure arithmetic — never shrinks,
    /// partitions, or formats anything.
    /// </summary>
    DevDriveResizeSimulation SimulateResize(DevDriveCreationPlan plan);
}
