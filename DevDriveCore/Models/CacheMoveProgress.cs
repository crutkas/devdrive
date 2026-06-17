namespace DevDriveCore.Models;

/// <summary>
/// Progress snapshot reported (via <see cref="System.IProgress{T}"/>) while
/// <see cref="DevDriveCore.Services.PackageCacheMover"/> copies a cache. Pure data.
/// </summary>
public sealed record CacheMoveProgress
{
    /// <summary>Number of files copied + verified so far.</summary>
    public int FilesCompleted { get; init; }

    /// <summary>Total number of files to copy.</summary>
    public int TotalFiles { get; init; }

    /// <summary>Number of bytes copied so far.</summary>
    public long BytesCompleted { get; init; }

    /// <summary>Total number of bytes to copy.</summary>
    public long TotalBytes { get; init; }

    /// <summary>The file currently being processed (full source path), or empty when finished.</summary>
    public string CurrentFile { get; init; } = string.Empty;

    /// <summary>Fraction complete in [0, 1], by bytes (falls back to file count when total bytes is 0).</summary>
    public double Fraction =>
        TotalBytes > 0 ? Math.Clamp((double)BytesCompleted / TotalBytes, 0d, 1d)
        : TotalFiles > 0 ? Math.Clamp((double)FilesCompleted / TotalFiles, 0d, 1d)
        : 0d;
}
