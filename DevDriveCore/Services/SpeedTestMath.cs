using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Compares two <see cref="DiskBenchmarkResult"/>s into a <see cref="SpeedTestComparison"/>. Pure
/// and deterministic (the headline is a geometric mean of the per-metric ratios), so it is fully
/// unit-testable without touching a disk.
/// </summary>
public static class SpeedTestMath
{
    /// <summary>Builds the comparison rows + headline multiplier from a system and Dev Drive result.</summary>
    public static SpeedTestComparison Build(DiskBenchmarkResult system, DiskBenchmarkResult dev, DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(dev);

        var metrics = new[]
        {
            new SpeedMetric { Kind = SpeedMetricKind.SequentialWrite, Name = "Sequential write", Unit = "MB/s", SystemValue = system.SequentialWriteMBps, DevValue = dev.SequentialWriteMBps },
            new SpeedMetric { Kind = SpeedMetricKind.SequentialRead, Name = "Sequential read", Unit = "MB/s", SystemValue = system.SequentialReadMBps, DevValue = dev.SequentialReadMBps },
            new SpeedMetric { Kind = SpeedMetricKind.RandomRead4K, Name = "Random 4K read", Unit = "IOPS", SystemValue = system.RandomRead4KIops, DevValue = dev.RandomRead4KIops },
            new SpeedMetric { Kind = SpeedMetricKind.RandomWrite4K, Name = "Random 4K write", Unit = "IOPS", SystemValue = system.RandomWrite4KIops, DevValue = dev.RandomWrite4KIops },
        };

        double headline = GeometricMean(metrics.Select(m => m.Ratio).ToArray());

        return new SpeedTestComparison
        {
            Metrics = metrics,
            HeadlineMultiplier = headline,
            CompletedAt = completedAt,
            SystemResult = system,
            DevResult = dev,
        };
    }

    /// <summary>
    /// Geometric mean of the positive, finite values in <paramref name="values"/>. Non-positive /
    /// non-finite entries are ignored (e.g. a metric whose system value was zero). Returns <c>0</c>
    /// when nothing usable remains.
    /// </summary>
    public static double GeometricMean(IReadOnlyList<double> values)
    {
        if (values is null || values.Count == 0)
        {
            return 0d;
        }

        double sumLogs = 0d;
        int count = 0;
        foreach (double value in values)
        {
            if (value <= 0d || double.IsNaN(value) || double.IsInfinity(value))
            {
                continue;
            }

            sumLogs += Math.Log(value);
            count++;
        }

        return count == 0 ? 0d : Math.Exp(sumLogs / count);
    }
}
