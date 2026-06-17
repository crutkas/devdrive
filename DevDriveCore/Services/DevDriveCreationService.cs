using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Orchestrates the Dev Drive creation flow on top of the UI-agnostic core. Splits cleanly along the
/// platform's two sources:
/// <list type="bullet">
///   <item><description><b>VHDX</b> — <see cref="CreateVhdDevDriveAsync"/> builds a
///   <see cref="VhdProvisionPlan"/> and runs it through an injected
///   <see cref="DevDriveCore.Abstractions.IVhdProvisioner"/> (create + attach the disk).</description></item>
///   <item><description><b>Resize</b> — <see cref="SimulateResize"/> only ever computes a
///   <see cref="DevDriveResizeSimulation"/> preview; it never calls a shrink/partition API.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <b>SAFETY:</b> the resize path is simulation-only by construction. The VHDX path is gated behind an
/// explicit user confirmation in the UI and never auto-executes; unit tests exercise it exclusively
/// against a mock <see cref="DevDriveCore.Abstractions.INativeVhdApi"/>, so no test creates, attaches,
/// or formats a real disk. Even when confirmed, this engine only creates and attaches the VHDX — the
/// ReFS dev-volume <i>format</i> (<c>Format-Volume -DevDrive</c>, which requires admin) is a separate,
/// not-yet-wired step, so <see cref="DevDriveCreationResult.FormatPending"/> is reported.
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
    /// Convenience factory wiring the REAL <see cref="VhdProvisioner"/>. The app composes this so a
    /// confirmed VHDX creation can run; nothing executes until the user confirms, and the resize path
    /// never touches it. Inject a different <see cref="DevDriveCore.Abstractions.IVhdProvisioner"/> to
    /// neutralise real I/O.
    /// </summary>
    public static DevDriveCreationService CreateDefault() => new(VhdProvisioner.CreateDefault());

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

        string disk = vhd.DiskNumber is int n ? $"disk {n}" : "the new disk";
        return new DevDriveCreationResult
        {
            Success = vhd.Success,
            Source = DevDriveCreationSource.Vhdx,
            VhdResult = vhd,
            ReversibilityId = vhd.ReversibilityId,
            FormatPending = true,
            Summary =
                $"Created and attached a raw {ByteSizeFormatter.Format(plan.SizeBytes)} VHDX at {plan.VhdFilePath} as {disk}. " +
                "Finish in Disk Management (admin): initialize the disk, create a partition, assign a letter, then format it as a ReFS Dev Drive.",
        };
    }

    /// <inheritdoc />
    public DevDriveResizeSimulation SimulateResize(DevDriveCreationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // PURE compute. Deliberately no Resize-Partition / New-Partition / Format-Volume call, no
        // IOCTL_DISK_SET_DRIVE_LAYOUT_EX — the resize path is preview-only in this build.
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
