using DevDriveCore.Models;

namespace DevDriveCore.Abstractions;

/// <summary>
/// The drive roots and a neutral seed location a real-workload benchmark runs against. The benchmark
/// stages its bounded fixtures under <see cref="SeedRoot"/> (a temp folder, not under test) and does
/// its measured churn under each drive root.
/// </summary>
/// <param name="SystemRoot">System drive root, e.g. <c>C:\</c>.</param>
/// <param name="DevRoot">Dev Drive root, e.g. <c>G:\</c>.</param>
/// <param name="SeedRoot">A neutral, writable folder for shared fixtures (e.g. under <c>%TEMP%</c>).</param>
public sealed record WorkloadEnvironment(string SystemRoot, string DevRoot, string SeedRoot);

/// <summary>
/// Thrown by an <see cref="IWorkloadBenchmark"/> to indicate it can't run and should be reported as
/// SKIPPED with the supplied reason (e.g. "npm: no network to populate the package cache"). The
/// orchestrator turns this into a skipped <see cref="DevDriveCore.Models.WorkloadMetric"/> rather than
/// failing the whole comparison.
/// </summary>
public sealed class WorkloadUnavailableException : Exception
{
    /// <summary>Creates the exception with a user-facing skip reason.</summary>
    public WorkloadUnavailableException(string message) : base(message) { }

    /// <summary>Creates the exception with a user-facing skip reason and an inner cause.</summary>
    public WorkloadUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// A single real developer-workload benchmark (git clone, npm ci, dotnet build, cargo build …).
/// </summary>
/// <remarks>
/// <para><b>SAFETY:</b> implementations may really run external tools, but ONLY against bounded
/// temporary fixtures created under the drive roots / seed root, and MUST delete everything they
/// create in <see cref="Cleanup"/> (and per-iteration). No machine-config, env-var, or
/// partition/format changes — ever.</para>
/// <para>This is the execution seam: orchestrator unit tests inject a fake, so the test suite never
/// launches a real process. The lifecycle is <see cref="Prepare"/> → <see cref="MeasureOnce"/> (N
/// times per drive) → <see cref="Cleanup"/>.</para>
/// </remarks>
public interface IWorkloadBenchmark
{
    /// <summary>Operation name shown to the user, e.g. "git clone".</summary>
    string Name { get; }

    /// <summary>Short fixture/operation description, e.g. "local bare repo · 15,000 files · cold first build".</summary>
    string Detail { get; }

    /// <summary>
    /// Selects the fixture size/realism: <see cref="WorkloadProfile.Quick"/> for the inline per-cache
    /// "Test speed" (moderate, responsive) and <see cref="WorkloadProfile.Thorough"/> for the global
    /// "Real workload test" card (realistic, real-world-app fixtures). The orchestrator sets this
    /// <em>before</em> reading <see cref="Detail"/> or running, so both reflect the chosen profile.
    /// </summary>
    WorkloadProfile Profile { get; set; }

    /// <summary>
    /// The developer tool this benchmark needs (matches an <see cref="IInstalledToolDetector"/>
    /// catalogue name, e.g. "git", "npm", "dotnet", "cargo"). The orchestrator skips the benchmark
    /// when the tool isn't installed.
    /// </summary>
    string RequiredTool { get; }

    /// <summary>
    /// Seeds the shared, bounded fixture(s) once before measurement. Throw
    /// <see cref="WorkloadUnavailableException"/> to skip this benchmark with a reason (e.g. no
    /// network to populate a cache, or insufficient free space).
    /// </summary>
    void Prepare(WorkloadEnvironment environment, CancellationToken cancellationToken);

    /// <summary>
    /// Runs exactly one cold first-build iteration against <paramref name="driveRoot"/> and returns its
    /// wall-clock seconds. The implementation creates its own bounded working directory under the drive,
    /// freshly (re)populates its per-drive package cache cold onto that same drive, performs the churn,
    /// times it, and deletes that working directory. Throw <see cref="WorkloadUnavailableException"/> to
    /// skip (e.g. the tool failed).
    /// </summary>
    double MeasureOnce(string driveRoot, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every fixture/working directory the benchmark created, on both drives and the seed
    /// root. Must be safe to call even if <see cref="Prepare"/> threw, and must never throw.
    /// </summary>
    void Cleanup();
}
