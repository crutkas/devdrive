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
/// <see cref="VerifyAndExecuteAsync"/> is the normal confirmed UI path: one elevated helper invocation
/// verifies and binds the live disk identity, revalidates it immediately before mutation, then performs
/// the destructive shrink &#8594; repartition &#8594; format sequence.
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
    /// Runs one confirmed elevated transaction that first verifies every live resize guard and binds the
    /// exact disk/partition identity, then rechecks that identity immediately before shrinking. A failed
    /// verification reports <see cref="ResizeExecuteOutcome.Executed"/> <c>= false</c> and changes nothing.
    /// </summary>
    Task<ResizeExecuteOutcome> VerifyAndExecuteAsync(
        ResizePlan plan,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the elevated destructive path for a plan that already carries a verified live identity.
    /// Retained for callers that explicitly use <see cref="PreviewAsync"/> before execution.
    /// </summary>
    Task<ResizeExecuteOutcome> ExecuteAsync(ResizePlan plan, CancellationToken cancellationToken = default);
}
