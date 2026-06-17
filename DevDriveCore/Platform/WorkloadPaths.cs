namespace DevDriveCore.Platform;

/// <summary>
/// Shared folder names for the real-workload benchmarks. Bounded temp fixtures live under these
/// names so cleanup can target them deterministically.
/// </summary>
internal static class WorkloadPaths
{
    /// <summary>Per-drive working area, created under each drive root, e.g. <c>G:\DevDriveManagerWorkloadBench</c>.</summary>
    public const string BenchFolderName = "DevDriveManagerWorkloadBench";

    /// <summary>Neutral shared-fixture area, created under <c>%TEMP%</c>.</summary>
    public const string SeedFolderName = "DevDriveManagerWorkloadSeed";
}
