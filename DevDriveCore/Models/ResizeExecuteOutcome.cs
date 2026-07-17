namespace DevDriveCore.Models;

/// <summary>
/// Result of the destructive resize (the broker's <see cref="ResizeMode.Execute"/> path): whether the
/// shrink &#8594; create-partition &#8594; <c>Format-Volume -DevDrive</c> sequence actually ran and
/// succeeded.
/// </summary>
/// <remarks>
/// <b>SAFETY:</b> <see cref="Executed"/> is <c>false</c> only when the result can guarantee that the
/// destructive path was not reached. It is also <c>true</c> for an uncertain result after the helper was
/// launched, so callers never encourage a retry when a mutation cannot be ruled out.
/// <see cref="Success"/> implies <see cref="Executed"/>.
/// </remarks>
public sealed record ResizeExecuteOutcome
{
    /// <summary>True only when the full resize sequence completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>
    /// True when the destructive sequence ran or cannot safely be ruled out. <c>false</c> guarantees
    /// that nothing was changed.
    /// </summary>
    public bool Executed { get; init; }

    /// <summary>True when disk mutation may have started but the final layout could not be verified.</summary>
    public bool StateUnknown { get; init; }

    /// <summary>Human-readable result or failure message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Letter (without the colon) of the source volume.</summary>
    public char SourceVolumeLetter { get; init; }

    /// <summary>Letter (without the colon) assigned to the new Dev Drive.</summary>
    public char NewDriveLetter { get; init; }

    /// <summary>Size in bytes of the Dev Drive that was (or would have been) created.</summary>
    public ulong DevDriveBytes { get; init; }

    /// <summary>Disk number read back for the created Dev Drive.</summary>
    public int? DiskNumber { get; init; }

    /// <summary>Partition number read back for the created Dev Drive.</summary>
    public int? PartitionNumber { get; init; }

    /// <summary>Filesystem read back for the created Dev Drive.</summary>
    public string FileSystem { get; init; } = string.Empty;

    /// <summary>True only when <c>fsutil devdrv query</c> verified the final volume.</summary>
    public bool IsDevDrive { get; init; }

    /// <summary>Ordered, human-readable steps that completed (or that would run, in a gated/dry path).</summary>
    public IReadOnlyList<string> CompletedSteps { get; init; } = Array.Empty<string>();
}
