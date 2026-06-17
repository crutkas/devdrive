namespace DevDriveCore.Models;

/// <summary>
/// Immutable, UI-agnostic description of a "shrink an existing volume to carve out a Dev Drive"
/// operation. This is the JSON payload the app hands to the elevated resize broker (the helper). A
/// plan is SAFE to build and serialize &#8212; nothing happens until the broker is invoked, and even
/// then only when <see cref="ResizeMode.Execute"/> is requested.
/// </summary>
/// <remarks>
/// The same shape is (de)serialized on both sides of the elevation boundary: the app serializes it,
/// the helper deserializes it, validates it with <see cref="DevDriveCore.Services.ResizeGuard"/>, and
/// either reports feasibility (<see cref="ResizeMode.WhatIf"/>) or runs the sequence
/// (<see cref="ResizeMode.Execute"/>). A crafted/garbage plan is rejected by the guard, never executed.
/// </remarks>
public sealed record ResizePlan
{
    /// <summary>Letter (without the colon) of the volume to shrink, e.g. <c>'C'</c>.</summary>
    public char SourceVolumeLetter { get; init; } = 'C';

    /// <summary>How many bytes to reclaim from the source and turn into the new Dev Drive partition.</summary>
    public ulong ShrinkBytes { get; init; }

    /// <summary>Letter (without the colon) to assign to the new Dev Drive, e.g. <c>'D'</c>.</summary>
    public char NewDriveLetter { get; init; } = 'D';

    /// <summary>Volume label for the new Dev Drive.</summary>
    public string Label { get; init; } = "DevDrive";

    /// <summary>
    /// Authorization flag the gated UI execute path sets to <c>true</c> (in
    /// <see cref="DevDriveCore.Services.VolumeResizer.ExecuteAsync"/>) immediately before the plan is
    /// serialized and handed to the elevated helper. The helper REFUSES a <c>--execute</c> request whose
    /// plan does not carry this flag, so a bare command-line <c>--execute</c> against a hand-written or
    /// preview (<c>--whatif</c>) plan can't trigger the destructive path.
    /// <para>
    /// <b>Defence in depth only.</b> A caller able to craft the plan JSON could also set this flag; it is
    /// NOT the security boundary. The real firewall remains the read-only <see cref="ResizeMode.WhatIf"/>
    /// default, ResizeGuard re-validation against a fresh disk snapshot, and the UAC elevation prompt.
    /// </para>
    /// </summary>
    public bool ExecuteAuthorized { get; init; }
}
