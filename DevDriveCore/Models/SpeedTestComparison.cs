namespace DevDriveCore.Models;

/// <summary>Identifies which of the four benchmarked operations a <see cref="SpeedMetric"/> describes.</summary>
public enum SpeedMetricKind
{
    /// <summary>Sequential write throughput (MB/s).</summary>
    SequentialWrite,

    /// <summary>Sequential read throughput (MB/s).</summary>
    SequentialRead,

    /// <summary>Random 4 KiB read throughput (IOPS).</summary>
    RandomRead4K,

    /// <summary>Random 4 KiB write throughput (IOPS).</summary>
    RandomWrite4K,
}

/// <summary>
/// One row of the speed-test comparison: the same metric measured on the system drive and the Dev
/// Drive, plus the higher-is-better ratio between them.
/// </summary>
public sealed record SpeedMetric
{
    /// <summary>Which operation this metric describes.</summary>
    public SpeedMetricKind Kind { get; init; }

    /// <summary>Human-readable label, e.g. "Sequential write".</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Unit suffix for display, e.g. "MB/s" or "IOPS".</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>Value measured on the system drive (C:).</summary>
    public double SystemValue { get; init; }

    /// <summary>Value measured on the Dev Drive.</summary>
    public double DevValue { get; init; }

    /// <summary>
    /// Dev Drive value divided by system value (higher is better for all four metrics). Returns
    /// <c>0</c> when the system value is non-positive so the headline math can ignore it.
    /// </summary>
    public double Ratio => SystemValue <= 0d ? 0d : DevValue / SystemValue;
}

/// <summary>
/// The result of comparing the Dev Drive against the system drive across the four benchmarked
/// operations, with an overall headline multiplier (geometric mean of the per-metric ratios).
/// </summary>
public sealed record SpeedTestComparison
{
    /// <summary>Per-metric comparison rows.</summary>
    public IReadOnlyList<SpeedMetric> Metrics { get; init; } = Array.Empty<SpeedMetric>();

    /// <summary>Geometric mean of the per-metric ratios — the "Nx faster on average" headline.</summary>
    public double HeadlineMultiplier { get; init; }

    /// <summary>When the comparison completed.</summary>
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>Raw system-drive measurement (for diagnostics / detail).</summary>
    public DiskBenchmarkResult? SystemResult { get; init; }

    /// <summary>Raw Dev Drive measurement (for diagnostics / detail).</summary>
    public DiskBenchmarkResult? DevResult { get; init; }
}
