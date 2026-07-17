using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveManager.Services;

/// <summary>
/// UI-test-only <see cref="IVhdProvisioner"/> that simulates complete VHDX Dev Drive creation. It returns
/// a plausible verified result without touching <c>virtdisk.dll</c>, Storage cmdlets, the filesystem,
/// or any reversibility store. Selected
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
            Executed = true,
            Message =
                $"Created and attached the VHDX, then initialized and formatted {plan.DriveLetter}: as a simulated ReFS Dev Drive.",
            FilePath = plan.FilePath,
            PhysicalPath = $@"\\.\PhysicalDrive{SimulatedDiskNumber}",
            DiskNumber = SimulatedDiskNumber,
            DriveLetter = char.ToUpperInvariant(plan.DriveLetter),
            SizeBytes = plan.VolumeSizeBytes,
            FileSystem = "ReFS",
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
