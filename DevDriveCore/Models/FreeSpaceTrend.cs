namespace DevDriveCore.Models;

/// <summary>One plotted reading, with its position already normalised to the 0..1 unit square.</summary>
/// <param name="X">0 at the oldest sample in the window, 1 at the newest.</param>
/// <param name="Y">0 at the bottom of the plotted range, 1 at the top.</param>
/// <param name="Sample">The reading this point came from.</param>
/// <param name="IsProjected">
/// True for the single synthetic point at the end of the line. Drawn dimmer, because it is arithmetic
/// rather than a measurement.
/// </param>
public sealed record FreeSpacePoint(double X, double Y, FreeSpaceSample Sample, bool IsProjected);

/// <summary>
/// What a volume's free space has been doing, and where the current rate lands it.
/// </summary>
/// <remarks>
/// The projection is a straight line through the oldest and newest samples in the window and nothing
/// cleverer. A regression over three points of a developer's disk would look more serious without
/// being more correct, and the caption says "at this rate" rather than "predicted" for that reason.
/// </remarks>
public sealed record FreeSpaceTrend
{
    /// <summary>The volume this describes.</summary>
    public string VolumeId { get; init; } = string.Empty;

    /// <summary>Measured points, oldest first, followed by the projected point when there is one.</summary>
    public IReadOnlyList<FreeSpacePoint> Points { get; init; } = [];

    /// <summary>The oldest reading in the window.</summary>
    public FreeSpaceSample? Oldest { get; init; }

    /// <summary>The newest reading in the window.</summary>
    public FreeSpaceSample? Latest { get; init; }

    /// <summary>
    /// Free bytes one window further on if the current rate holds, or <see langword="null"/> when
    /// there is not enough history to state a rate.
    /// </summary>
    public long? ProjectedFreeBytes { get; init; }

    /// <summary>
    /// Change in free bytes across the window. Negative means the volume is filling up. Zero when
    /// there is only one reading.
    /// </summary>
    public long DeltaBytes { get; init; }

    /// <summary>How long the readings actually span. Not the requested window — what we really have.</summary>
    public TimeSpan Span { get; init; }

    /// <summary>How many measured readings are in the window.</summary>
    public int SampleCount { get; init; }

    /// <summary>
    /// True once there are at least two readings far enough apart to draw a line between. Below this
    /// the room shows what it is doing ("recording since…") instead of a chart of one point, which
    /// would imply a flat trend nobody measured.
    /// </summary>
    public bool HasTrend => SampleCount >= 2;

    /// <summary>An empty trend for a volume with no history at all.</summary>
    public static FreeSpaceTrend Empty(string volumeId) => new() { VolumeId = volumeId };
}
