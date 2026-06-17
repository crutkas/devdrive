namespace DevDriveCore.Models;

/// <summary>
/// Result of a READ-ONLY resize feasibility check (the broker's <see cref="ResizeMode.WhatIf"/> path):
/// the go/no-go verdict, why, and exactly what the real operation WOULD do. Produced by
/// <see cref="DevDriveCore.Services.ResizeGuard.Evaluate"/>; computing one touches nothing on the
/// machine.
/// </summary>
public sealed record ResizeFeasibility
{
    /// <summary>True when every guard passes and the resize is safe to perform.</summary>
    public bool CanProceed { get; init; }

    /// <summary>Human-readable explanation of the verdict (especially the reason for a no-go).</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Letter (without the colon) of the source volume that would be shrunk.</summary>
    public char SourceVolumeLetter { get; init; }

    /// <summary>Letter (without the colon) the new Dev Drive would receive.</summary>
    public char NewDriveLetter { get; init; }

    /// <summary>Bytes the plan asked to shrink/reclaim.</summary>
    public ulong RequestedShrinkBytes { get; init; }

    /// <summary>The requested shrink after disk-alignment rounding &#8212; the actual Dev Drive size that would be carved.</summary>
    public ulong AlignedShrinkBytes { get; init; }

    /// <summary>Real reclaimable space on the source (<c>partition size &#8722; supported minimum</c>).</summary>
    public ulong ReclaimableBytes { get; init; }

    /// <summary>Source partition size before the shrink.</summary>
    public ulong SourceSizeBytesBefore { get; init; }

    /// <summary>Source partition size after the shrink.</summary>
    public ulong SourceSizeBytesAfter { get; init; }

    /// <summary>Ordered, human-readable description of what the real (elevated) operation would perform.</summary>
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    /// <summary>Always <c>true</c> for a feasibility check: producing this preview never mutates anything.</summary>
    public bool IsReadOnlyProbe { get; init; } = true;
}
