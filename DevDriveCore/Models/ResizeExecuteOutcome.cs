namespace DevDriveCore.Models;

/// <summary>
/// Result of the destructive resize (the broker's <see cref="ResizeMode.Execute"/> path): whether the
/// shrink &#8594; create-partition &#8594; <c>Format-Volume -DevDrive</c> sequence actually ran and
/// succeeded.
/// </summary>
/// <remarks>
/// <b>SAFETY:</b> <see cref="Executed"/> is <c>false</c> whenever the guards rejected the plan, the
/// helper was unavailable, the user declined elevation, or the destructive path was otherwise not
/// reached &#8212; i.e. when NOTHING was touched. <see cref="Success"/> implies <see cref="Executed"/>.
/// </remarks>
public sealed record ResizeExecuteOutcome
{
    /// <summary>True only when the full resize sequence completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>True when the real, destructive sequence actually ran. <c>false</c> means nothing was changed.</summary>
    public bool Executed { get; init; }

    /// <summary>Human-readable result or failure message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Letter (without the colon) of the source volume.</summary>
    public char SourceVolumeLetter { get; init; }

    /// <summary>Letter (without the colon) assigned to the new Dev Drive.</summary>
    public char NewDriveLetter { get; init; }

    /// <summary>Size in bytes of the Dev Drive that was (or would have been) created.</summary>
    public ulong DevDriveBytes { get; init; }

    /// <summary>Ordered, human-readable steps that completed (or that would run, in a gated/dry path).</summary>
    public IReadOnlyList<string> CompletedSteps { get; init; } = Array.Empty<string>();
}
