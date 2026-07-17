using DevDriveCore.Models;

namespace DevDriveCore.Abstractions;

/// <summary>
/// Provisions a reversible VHDX-backed Dev Drive.
/// </summary>
/// <remarks>
/// <para>Composed by the app (M8) and run only after an explicit user confirmation; the real native
/// layer is <see cref="DevDriveCore.Platform.NativeVhdApi"/>. Under the UI-test seam a SAFE fake
/// provisioner is substituted, so the automated suite never creates a real disk.</para>
/// <para><b>SAFETY:</b> every unit test injects a mock <see cref="INativeVhdApi"/>; no test creates,
/// attaches, or detaches a real VHD.</para>
/// </remarks>
public interface IVhdProvisioner
{
    /// <summary>
    /// Provisions the <c>.vhdx</c> described by <paramref name="plan"/> and records a
    /// <see cref="ReversibilityEntry"/> so the operation can be undone with <see cref="RevertAsync"/>.
    /// </summary>
    Task<VhdProvisionResult> ProvisionAsync(VhdProvisionPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Reverts a provisioning recorded as <paramref name="entry"/> (detach the disk, delete the file).</summary>
    Task RevertAsync(ReversibilityEntry entry, CancellationToken cancellationToken = default);
}
