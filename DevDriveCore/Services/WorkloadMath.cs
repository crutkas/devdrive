using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Pure, deterministic math for the real-workload comparison: median (discarding the first warm-up
/// run, which primes process start-up / JIT / OS state), per-operation rows, and the headline speedup
/// (median of the per-operation speedups). No I/O, so it is fully unit-testable without launching a
/// process.
/// </summary>
public static class WorkloadMath
{
    /// <summary>
    /// Median of <paramref name="values"/>. For an even count, the mean of the two middle values.
    /// Returns <c>0</c> for an empty/null list. Does not mutate the input.
    /// </summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values is null || values.Count == 0)
        {
            return 0d;
        }

        double[] sorted = values.ToArray();
        Array.Sort(sorted);

        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2d;
    }

    /// <summary>
    /// Reports the representative time from a set of cold first-build runs: discards the first run when
    /// there are at least two (it primes process start-up / JIT / OS state), then takes the
    /// <see cref="Median"/> of the rest. With a single run there is nothing to discard, so that run is
    /// returned as-is.
    /// </summary>
    public static double MedianAfterDiscardingFirst(IReadOnlyList<double> runs)
    {
        if (runs is null || runs.Count == 0)
        {
            return 0d;
        }

        if (runs.Count == 1)
        {
            return runs[0];
        }

        return Median(runs.Skip(1).ToArray());
    }

    /// <summary>
    /// Builds a measured row from the raw system and Dev Drive run times. Each median discards the
    /// first run (see <see cref="MedianAfterDiscardingFirst"/>). The raw runs are preserved on the
    /// metric for transparency.
    /// </summary>
    public static WorkloadMetric BuildMetric(string name, string detail, IReadOnlyList<double> systemRuns, IReadOnlyList<double> devRuns, double prepareSeconds = 0d, double setupSeconds = 0d)
    {
        IReadOnlyList<double> system = systemRuns ?? Array.Empty<double>();
        IReadOnlyList<double> dev = devRuns ?? Array.Empty<double>();

        return new WorkloadMetric
        {
            Name = name,
            Detail = detail,
            Skipped = false,
            SystemSeconds = MedianAfterDiscardingFirst(system),
            DevSeconds = MedianAfterDiscardingFirst(dev),
            SystemRuns = system,
            DevRuns = dev,
            PrepareSeconds = Math.Max(0d, prepareSeconds),
            SetupSeconds = Math.Max(0d, setupSeconds),
        };
    }

    /// <summary>Builds a skipped row (tool missing, no network, low disk, or an error) with a reason.</summary>
    public static WorkloadMetric Skipped(string name, string detail, string reason) => new()
    {
        Name = name,
        Detail = detail,
        Skipped = true,
        SkipReason = reason,
    };

    /// <summary>
    /// Assembles the final comparison. The headline is the <see cref="Median"/> of the per-operation
    /// <see cref="WorkloadMetric.Speedup"/> values across operations that actually ran (skipped rows
    /// and non-positive speedups are excluded). The headline is <c>0</c> when nothing ran.
    /// </summary>
    public static WorkloadComparison Build(IReadOnlyList<WorkloadMetric> metrics, DateTimeOffset completedAt, PreflightInfo preflight)
    {
        IReadOnlyList<WorkloadMetric> rows = metrics ?? Array.Empty<WorkloadMetric>();

        double[] speedups = rows
            .Where(m => !m.Skipped && m.Speedup > 0d)
            .Select(m => m.Speedup)
            .ToArray();

        return new WorkloadComparison
        {
            Metrics = rows,
            HeadlineSpeedup = Median(speedups),
            CompletedAt = completedAt,
            Preflight = preflight ?? new PreflightInfo(),
        };
    }
}
