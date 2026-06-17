namespace DevDriveCore.Models;

/// <summary>
/// The result of comparing the Dev Drive against the system drive across the real developer-workload
/// benchmarks (git clone, npm ci, dotnet build, cargo build …). Distinct from
/// <see cref="SpeedTestComparison"/> (synthetic raw-I/O, higher-is-better MB/s): these rows are
/// time-based (lower is better) and the headline is the <b>median of the per-operation speedups</b>.
/// </summary>
public sealed record WorkloadComparison
{
    /// <summary>Per-operation rows, in run order. Includes skipped operations (with a reason).</summary>
    public IReadOnlyList<WorkloadMetric> Metrics { get; init; } = Array.Empty<WorkloadMetric>();

    /// <summary>
    /// Median of the per-operation <see cref="WorkloadMetric.Speedup"/> values across the operations
    /// that actually ran. <c>0</c> when nothing ran. Median (not mean) so one outlier can't dominate.
    /// </summary>
    public double HeadlineSpeedup { get; init; }

    /// <summary>When the comparison completed.</summary>
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>Pre-flight context (Defender performance mode, trust, storage class, machine, free space).</summary>
    public PreflightInfo Preflight { get; init; } = new();

    /// <summary>How many operations actually produced a measurement.</summary>
    public int CompletedCount => Metrics.Count(m => !m.Skipped);

    /// <summary>How many operations were skipped (tool missing, no network, low disk, error).</summary>
    public int SkippedCount => Metrics.Count(m => m.Skipped);
}
