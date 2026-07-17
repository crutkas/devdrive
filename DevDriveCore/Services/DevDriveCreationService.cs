using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Orchestrates the Dev Drive creation flow on top of the UI-agnostic core. Splits cleanly along the
/// platform's two sources:
/// <list type="bullet">
///   <item><description><b>VHDX</b> — <see cref="CreateVhdDevDriveAsync"/> builds a
///   <see cref="VhdProvisionPlan"/> and runs it through an injected
///   <see cref="DevDriveCore.Abstractions.IVhdProvisioner"/>.</description></item>
///   <item><description><b>Resize</b> — <see cref="SimulateResize"/> only ever computes a
///   <see cref="DevDriveResizeSimulation"/> preview; it never calls a shrink/partition API.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <b>SAFETY:</b> the resize method here remains pure preview arithmetic. The VHDX path runs only after
/// explicit UI confirmation; production delegates the complete operation to the bundled elevated helper,
/// while unit and UI tests inject safe fakes and never touch a real disk.
/// </remarks>
public sealed class DevDriveCreationService : IDevDriveCreationService
{
    private readonly Abstractions.IVhdProvisioner _provisioner;

    /// <summary>Creates the service over an injected provisioner (tests pass one backed by a mock native API).</summary>
    public DevDriveCreationService(Abstractions.IVhdProvisioner provisioner)
    {
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
    }

    /// <summary>
    /// Convenience factory wiring the complete elevated VHDX provisioner.
    /// </summary>
    public static DevDriveCreationService CreateDefault() => new(ElevatedVhdProvisioner.CreateDefault());

    /// <inheritdoc />
    public async Task<DevDriveCreationResult> CreateVhdDevDriveAsync(DevDriveCreationPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Source != DevDriveCreationSource.Vhdx)
        {
            throw new ArgumentException("CreateVhdDevDriveAsync requires a VHDX plan.", nameof(plan));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(plan.VhdFilePath);
        if (plan.SizeBytes < DevDriveSizeMath.MinimumSizeBytesExact)
        {
            throw new ArgumentOutOfRangeException(nameof(plan), "Dev Drive size must be at least 50 GiB.");
        }

        VhdProvisionResult vhd = await _provisioner
            .ProvisionAsync(plan.ToVhdProvisionPlan(), cancellationToken)
            .ConfigureAwait(false);

        return new DevDriveCreationResult
        {
            Success = vhd.Success,
            Source = DevDriveCreationSource.Vhdx,
            VhdResult = vhd,
            ReversibilityId = vhd.ReversibilityId,
            StateUnknown = vhd.StateUnknown,
            Summary = string.IsNullOrWhiteSpace(vhd.Message)
                ? vhd.Success
                    ? $"Created {plan.DriveLetter}: as a ReFS Dev Drive backed by {plan.VhdFilePath}."
                    : "The VHDX Dev Drive was not created."
                : vhd.Message,
        };
    }

    /// <inheritdoc />
    public DevDriveResizeSimulation SimulateResize(DevDriveCreationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // PURE compute. Deliberately no Resize-Partition / New-Partition / Format-Volume call, no
        // IOCTL_DISK_SET_DRIVE_LAYOUT_EX — this method is always a preview; execution uses IVolumeResizer.
        ulong shrink = plan.SizeBytes;
        char source = plan.SourceVolumeLetter ?? 'C';
        ulong newSize = plan.SourceVolumeSizeBytes > shrink ? plan.SourceVolumeSizeBytes - shrink : 0UL;
        ulong newFree = plan.SourceVolumeFreeBytes > shrink ? plan.SourceVolumeFreeBytes - shrink : 0UL;

        var steps = new[]
        {
            $"Shrink the {source}: partition by {ByteSizeFormatter.Format(shrink)} with Resize-Partition (this also shrinks the file system).",
            $"Carve a new {ByteSizeFormatter.Format(shrink)} partition from the freed space and assign {plan.DriveLetter}: with New-Partition.",
            $"Format {plan.DriveLetter}: as ReFS with the Dev Drive flag (Format-Volume -DevDrive; requires admin).",
        };

        return new DevDriveResizeSimulation
        {
            SourceVolumeLetter = source,
            DevDriveLetter = plan.DriveLetter,
            ShrinkBytes = shrink,
            DevDriveBytes = shrink,
            NewSourceSizeBytes = newSize,
            NewSourceFreeBytes = newFree,
            Steps = steps,
            IsSimulationOnly = true,
        };
    }
}
