using DevDriveCore.Models;

namespace DevDriveCore.Abstractions;

/// <summary>
/// Carves a Dev Drive out of an existing volume by shrinking it &#8212; the resize counterpart to
/// <see cref="IVhdProvisioner"/>. Behind an interface so it is mockable: the app composes the
/// production implementation (which talks to the elevated helper), unit tests inject a mock, and the
/// UI-test seam injects a SAFE fake. No mock or fake ever touches a real disk.
/// </summary>
/// <remarks>
/// <para>
/// <b>SAFETY.</b> <see cref="PreviewAsync"/> is a READ-ONLY feasibility check (the helper's
/// <c>--whatif</c> mode): it queries real reclaimable space and runs every guard, but mutates nothing.
/// <see cref="ExecuteAsync"/> is the real, destructive shrink &#8594; repartition &#8594; format and
/// must stay behind a successful live preview + explicit user confirmation + UAC elevation.
/// </para>
/// </remarks>
public interface IVolumeResizer
{
    /// <summary>
    /// Runs a READ-ONLY feasibility check for <paramref name="plan"/> via the elevated helper's
    /// <c>--whatif</c> mode (real reclaimable space + every guard) and returns the verdict, or
    /// <c>null</c> when the elevated check couldn't run (helper unavailable, UAC declined, timed out,
    /// or unparseable) so the caller can fall back to a pure-compute estimate.
    /// </summary>
    Task<ResizeFeasibility?> PreviewAsync(ResizePlan plan, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the REAL, destructive resize (shrink &#8594; create partition &#8594; assign letter &#8594;
    /// <c>Format-Volume -DevDrive</c>) via the elevated helper's <c>--execute</c> mode. The guards run
    /// again inside the helper, so a rejected plan reports <see cref="ResizeExecuteOutcome.Executed"/>
    /// <c>= false</c> and changes nothing.
    /// </summary>
    Task<ResizeExecuteOutcome> ExecuteAsync(ResizePlan plan, CancellationToken cancellationToken = default);
}
