namespace DevDriveCore.Models;

/// <summary>
/// Which phase of a single real-workload benchmark a <see cref="WorkloadRunProgress"/> describes.
/// The lifecycle is <see cref="Preparing"/> (seeding the bounded fixture) → <see cref="SystemDrive"/>
/// (N timed runs on the system drive) → <see cref="DevDrive"/> (N timed runs on the Dev Drive).
/// </summary>
public enum WorkloadRunStage
{
    /// <summary>Seeding the bounded temp fixture before any measurement.</summary>
    Preparing,

    /// <summary>Running a timed iteration against the system drive.</summary>
    SystemDrive,

    /// <summary>Running a timed iteration against the Dev Drive.</summary>
    DevDrive,
}

/// <summary>
/// Fine-grained, live progress for a <em>single</em> real-workload benchmark (the inline per-cache
/// "Test speed"). Reported once per phase transition / per timed iteration so the UI can show the run
/// advancing in place (e.g. "System drive: run 2/3…"). Unlike the whole-comparison progress (one
/// <see cref="WorkloadMetric"/> per completed benchmark), this is per-iteration. Pure data.
/// </summary>
/// <param name="Stage">Which phase is currently executing.</param>
/// <param name="Run">1-based index of the current timed run within <paramref name="Stage"/>; <c>0</c> while preparing.</param>
/// <param name="TotalRuns">Total timed runs per drive for this benchmark (the iteration count).</param>
public sealed record WorkloadRunProgress(WorkloadRunStage Stage, int Run, int TotalRuns);
