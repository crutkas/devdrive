using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>Tunables for a real-workload comparison run.</summary>
public sealed record WorkloadBenchmarkOptions
{
    /// <summary>
    /// Timed iterations per drive (default 3). Each is a cold first build — the per-drive package cache
    /// is freshly re-populated before every run. The first is discarded and the median of the rest is
    /// reported (see the methodology). Must be at least 1. The Thorough fixtures are heavy
    /// (e.g. ~15,000-file git clones), so 3 keeps a full run bounded while preserving discard-first +
    /// median; the per-row Run passes its own small count explicitly.
    /// </summary>
    public int Iterations { get; init; } = 3;

    /// <summary>
    /// Fixture profile for the GLOBAL "Real workload test" card AND each per-row Run (default
    /// <see cref="WorkloadProfile.Thorough"/>): realistic, real-world-app fixtures that churn many
    /// small files so the Dev Drive's advantage actually shows.
    /// </summary>
    public WorkloadProfile GlobalProfile { get; init; } = WorkloadProfile.Thorough;

    /// <summary>The default option set.</summary>
    public static WorkloadBenchmarkOptions Default => new();
}

/// <summary>
/// Orchestrates the real developer-workload comparison: gates each benchmark on the tools installed
/// on this machine, captures pre-flight context, runs every available benchmark as a cold first build
/// N times per drive (discard-first, median), and folds the rows into a <see cref="WorkloadComparison"/>.
/// </summary>
public interface IWorkloadBenchmarkService
{
    /// <summary>
    /// Runs the comparison on a background thread. <paramref name="devDriveLetter"/> is the Dev Drive
    /// letter without a colon (e.g. <c>'G'</c>). When <paramref name="progress"/> is supplied each
    /// per-operation row is reported as soon as it completes (so the UI can show results live — git
    /// first). Each unavailable benchmark becomes a skipped row (with a reason) rather than failing
    /// the whole comparison; only cancellation aborts the run.
    /// </summary>
    Task<WorkloadComparison> RunAsync(
        string systemDriveRoot,
        string devDriveRoot,
        char devDriveLetter,
        IProgress<WorkloadMetric>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a <em>single</em> benchmark — the one whose <c>RequiredTool</c> matches
    /// <paramref name="requiredTool"/> (e.g. "npm", "dotnet", "cargo") — on a background thread, and
    /// returns its one <see cref="WorkloadMetric"/> ("what does moving THIS cache to the Dev Drive get
    /// me?"). Backs the performance suite's per-row Run — each row's Run button measures just that tool's
    /// cache, using the same fixture profile (<see cref="WorkloadBenchmarkOptions.GlobalProfile"/>) as the
    /// global "Run tests".
    /// <para>
    /// <paramref name="iterations"/> is the cold first-build run count per drive (the first is discarded,
    /// the median of the rest reported); pass a small value (e.g. 3) for a responsive per-row Run, or
    /// <c>0</c> to use the service's configured default. When <paramref name="progress"/> is supplied,
    /// each phase/iteration is reported (Preparing → System drive runs → Dev Drive runs) so the row can
    /// show live status. A missing benchmark, an uninstalled tool, no network, low disk, or any tool
    /// failure becomes a <em>skipped</em> metric (with a reason) rather than throwing; only cancellation
    /// aborts the run.
    /// </para>
    /// </summary>
    Task<WorkloadMetric> RunSingleAsync(
        string requiredTool,
        string systemDriveRoot,
        string devDriveRoot,
        char devDriveLetter,
        int iterations = 0,
        IProgress<WorkloadRunProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
