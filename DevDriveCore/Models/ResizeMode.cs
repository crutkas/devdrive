namespace DevDriveCore.Models;

/// <summary>
/// Which mode the elevated resize broker runs in. <see cref="WhatIf"/> is the safe default: a
/// READ-ONLY feasibility check that touches nothing. <see cref="Execute"/> is the real, destructive
/// shrink &#8594; create-partition &#8594; format sequence and must be requested explicitly.
/// </summary>
/// <remarks>
/// <b>SAFETY:</b> the broker defaults every code path to <see cref="WhatIf"/>. The only way to reach
/// <see cref="Execute"/> is to pass it deliberately after a successful live preview, a second explicit
/// user confirmation, and UAC elevation.
/// </remarks>
public enum ResizeMode
{
    /// <summary>Read-only feasibility check (default). Queries supported sizes, runs the guards, touches nothing.</summary>
    WhatIf = 0,

    /// <summary>The real, destructive resize: shrink the source, carve a partition, format it as a Dev Drive.</summary>
    Execute = 1,
}
