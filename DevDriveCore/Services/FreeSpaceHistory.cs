using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// The arithmetic behind the Overview room's free-space chart: thinning readings on the way in, and
/// turning what survives into a plottable trend on the way out.
/// </summary>
/// <remarks>
/// Split from the file that stores the readings so the interesting half has no I/O in it. Every rule
/// here — how often to keep a sample, how far back to look, how to project — is a product decision
/// that wants a test, and none of them want a temp directory.
/// </remarks>
public static class FreeSpaceHistory
{
    /// <summary>
    /// How close together two readings for the same volume can be before the newer one is treated as
    /// a duplicate. Volumes are re-enumerated on every navigation, so without this a busy session
    /// would write hundreds of readings a day and the chart would be a picture of how often the user
    /// clicked around.
    /// </summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(30);

    /// <summary>How many readings to keep per volume. At the minimum interval this is several months.</summary>
    public const int MaxSamplesPerVolume = 400;

    /// <summary>
    /// Adds a reading to the history, dropping it when an existing reading for the same volume is
    /// newer than <see cref="MinimumInterval"/>, and trimming the oldest readings past the cap.
    /// </summary>
    /// <returns>
    /// The new history, oldest first. The input is never mutated — callers hold the previous list
    /// while this runs, and a store that rewrites its caller's array is a bug that only shows up
    /// under a race.
    /// </returns>
    public static IReadOnlyList<FreeSpaceSample> Append(
        IReadOnlyList<FreeSpaceSample> existing,
        FreeSpaceSample sample)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(sample);

        List<FreeSpaceSample> ordered = [.. existing.OrderBy(s => s.TakenAtUtc)];

        FreeSpaceSample? newestForVolume = ordered
            .Where(s => string.Equals(s.VolumeId, sample.VolumeId, StringComparison.OrdinalIgnoreCase))
            .LastOrDefault();

        if (newestForVolume is not null
            && sample.TakenAtUtc - newestForVolume.TakenAtUtc < MinimumInterval)
        {
            return ordered;
        }

        ordered.Add(sample);

        // Trim per volume rather than globally: a machine with one busy volume and one idle one
        // should not lose the idle volume's entire history to the busy one's readings.
        List<FreeSpaceSample> overflow = [.. ordered
            .GroupBy(s => s.VolumeId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.OrderByDescending(s => s.TakenAtUtc).Skip(MaxSamplesPerVolume))];

        if (overflow.Count > 0)
        {
            ordered = [.. ordered.Except(overflow)];
        }

        return ordered;
    }

    /// <summary>
    /// Describes one volume's recent history as a plottable trend.
    /// </summary>
    /// <param name="samples">The whole history, any order, any volume.</param>
    /// <param name="volumeId">Which volume to describe.</param>
    /// <param name="now">The current time, passed in so the result is testable.</param>
    /// <param name="window">How far back to look. Readings older than this are ignored.</param>
    public static FreeSpaceTrend Describe(
        IReadOnlyList<FreeSpaceSample> samples,
        string volumeId,
        DateTimeOffset now,
        TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);

        List<FreeSpaceSample> inWindow = [.. samples
            .Where(s => string.Equals(s.VolumeId, volumeId, StringComparison.OrdinalIgnoreCase))
            .Where(s => now - s.TakenAtUtc <= window)
            .OrderBy(s => s.TakenAtUtc)];

        if (inWindow.Count == 0)
        {
            return FreeSpaceTrend.Empty(volumeId);
        }

        FreeSpaceSample oldest = inWindow[0];
        FreeSpaceSample latest = inWindow[^1];
        TimeSpan span = latest.TakenAtUtc - oldest.TakenAtUtc;

        if (inWindow.Count == 1)
        {
            return new FreeSpaceTrend
            {
                VolumeId = volumeId,
                Oldest = oldest,
                Latest = latest,
                SampleCount = 1,
                Span = TimeSpan.Zero,
            };
        }

        long delta = latest.FreeBytes - oldest.FreeBytes;

        // Project the same distance forward that the readings already cover, so "in as long again"
        // is a claim the data can carry. Extrapolating 30 days from 40 minutes of history would be
        // arithmetic dressed up as a forecast.
        long? projected = null;
        if (span > TimeSpan.Zero)
        {
            double perTick = delta / span.TotalSeconds;
            double forward = latest.FreeBytes + (perTick * span.TotalSeconds);
            projected = (long)Math.Clamp(forward, 0d, latest.TotalBytes);
        }

        List<long> values = [.. inWindow.Select(s => s.FreeBytes)];
        if (projected is long p)
        {
            values.Add(p);
        }

        // Plot against the observed range, not against zero: a 61 GB drop on a 976 GB volume is
        // invisible on a zero-based axis, and invisible is the wrong answer for a chart whose only
        // job is to show that it moved. The captions carry the absolute numbers.
        long min = values.Min();
        long max = values.Max();
        long range = max - min;

        double YFor(long v) => range == 0 ? 0.5d : (double)(v - min) / range;

        double totalSeconds = span.TotalSeconds;
        List<FreeSpacePoint> points = [];
        foreach (FreeSpaceSample s in inWindow)
        {
            double x = totalSeconds == 0
                ? 0d
                : (s.TakenAtUtc - oldest.TakenAtUtc).TotalSeconds / totalSeconds;

            // Measured points occupy the first half when a projection follows, so the projected
            // segment reads as "the same again" rather than as a rounding error at the right edge.
            points.Add(new FreeSpacePoint(projected is null ? x : x / 2d, YFor(s.FreeBytes), s, false));
        }

        if (projected is long value)
        {
            FreeSpaceSample synthetic = latest with
            {
                TakenAtUtc = latest.TakenAtUtc + span,
                FreeBytes = value,
            };
            points.Add(new FreeSpacePoint(1d, YFor(value), synthetic, true));
        }

        return new FreeSpaceTrend
        {
            VolumeId = volumeId,
            Points = points,
            Oldest = oldest,
            Latest = latest,
            ProjectedFreeBytes = projected,
            DeltaBytes = delta,
            Span = span,
            SampleCount = inWindow.Count,
        };
    }
}
