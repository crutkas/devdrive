using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveManager.Services;

/// <summary>
/// UI-test-only <see cref="IVhdProvisioner"/> that SIMULATES creating and surfacing a VHDX: it returns a
/// plausible success result (a synthetic disk number) WITHOUT touching <c>virtdisk.dll</c>, the
/// filesystem, or any reversibility store — no real disk is created, attached, or recorded. Selected
/// only when <c>DDM_UITEST_SAFE_MUTATIONS=1</c>; never used in normal operation.
/// </summary>
public sealed class SafeFakeVhdProvisioner : IVhdProvisioner
{
    // A fixed synthetic disk number so the completion copy reads realistically ("attached … as disk 9").
    private const int SimulatedDiskNumber = 9;

    /// <inheritdoc />
    public Task<VhdProvisionResult> ProvisionAsync(VhdProvisionPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new VhdProvisionResult
        {
            Success = true,
            FilePath = plan.FilePath,
            PhysicalPath = $@"\\.\PhysicalDrive{SimulatedDiskNumber}",
            DiskNumber = SimulatedDiskNumber,
            ReversibilityId = VhdProvisioner.ReversibilityId(plan.FilePath),
        });
    }

    /// <inheritdoc />
    public Task RevertAsync(ReversibilityEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Task.CompletedTask;
    }
}
