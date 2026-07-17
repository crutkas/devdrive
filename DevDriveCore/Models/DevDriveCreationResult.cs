namespace DevDriveCore.Models;

/// <summary>
/// Outcome of the Dev Drive creation flow. Pure data.
/// </summary>
public sealed record DevDriveCreationResult
{
    /// <summary>True when the operation (VHDX provisioning) — or the simulation — completed.</summary>
    public bool Success { get; init; }

    /// <summary>Which source produced this result.</summary>
    public DevDriveCreationSource Source { get; init; }

    /// <summary>One-line, human-readable summary of what happened (or would happen).</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>True when privileged work started but final disk state could not be confirmed.</summary>
    public bool StateUnknown { get; init; }

    /// <summary>The complete provisioning result for a VHDX source; <c>null</c> for a resize.</summary>
    public VhdProvisionResult? VhdResult { get; init; }

    /// <summary>The computed preview for a resize source; <c>null</c> for a VHDX.</summary>
    public DevDriveResizeSimulation? Simulation { get; init; }

    /// <summary><see cref="ReversibilityEntry.Id"/> recorded for an undoable VHDX provisioning, when any.</summary>
    public string? ReversibilityId { get; init; }
}
