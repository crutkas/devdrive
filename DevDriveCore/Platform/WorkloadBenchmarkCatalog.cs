using DevDriveCore.Abstractions;

namespace DevDriveCore.Platform;

/// <summary>
/// Builds the default set of real-workload benchmarks in priority order: git clone first (always
/// available, offline, universal), then npm ci, dotnet build, and cargo build. The orchestrator gates
/// each on whether its tool is installed.
/// </summary>
public static class WorkloadBenchmarkCatalog
{
    /// <summary>Creates the default benchmarks over the supplied process runner.</summary>
    public static IReadOnlyList<IWorkloadBenchmark> CreateDefault(IWorkloadProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);

        return new IWorkloadBenchmark[]
        {
            new GitCloneWorkload(runner),
            new NpmCiWorkload(runner),
            new DotnetBuildWorkload(runner),
            new CargoBuildWorkload(runner),
        };
    }
}
