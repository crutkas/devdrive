namespace DevDriveCore.Models;

/// <summary>
/// One reading of a volume's free space at a point in time.
/// </summary>
/// <remarks>
/// The Overview room wants to answer "am I losing ground?", and that question needs history that no
/// single API call can supply — Windows does not keep a free-space time series. So the app keeps its
/// own: a sample per volume each time volumes are enumerated, thinned to one per interval.
/// <para>
/// Deliberately a flat record of primitives rather than a reference to <see cref="VolumeInfo"/>: this
/// is written to disk and read back weeks later, and a persisted shape that points at a live model is
/// a schema migration waiting to happen.
/// </para>
/// </remarks>
public sealed record FreeSpaceSample
{
    /// <summary>When the reading was taken, in UTC.</summary>
    public DateTimeOffset TakenAtUtc { get; init; }

    /// <summary>
    /// Which volume this reading is for. Drive letter with colon (<c>"G:"</c>) where one exists,
    /// otherwise the volume label — the same identity the rooms show, so history survives a relabel
    /// no better and no worse than the UI does.
    /// </summary>
    public string VolumeId { get; init; } = string.Empty;

    /// <summary>Volume capacity in bytes at the time of the reading.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Free bytes at the time of the reading.</summary>
    public long FreeBytes { get; init; }

    /// <summary>Used bytes, clamped at zero.</summary>
    public long UsedBytes => TotalBytes >= FreeBytes ? TotalBytes - FreeBytes : 0L;
}
