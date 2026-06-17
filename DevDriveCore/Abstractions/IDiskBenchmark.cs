using DevDriveCore.Models;

namespace DevDriveCore.Abstractions;

/// <summary>
/// Tunables for a single benchmark run. Defaults keep the run modest (≈128 MiB, a few seconds per
/// drive) so the speed test is safe to run interactively and never fills the disk.
/// </summary>
public sealed record DiskBenchmarkOptions
{
    /// <summary>Total bytes written/read for the sequential passes (default 128 MiB). Rounded down to a 4 KiB multiple.</summary>
    public long FileSizeBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>Block size for the sequential passes (default 1 MiB).</summary>
    public int SequentialBlockBytes { get; init; } = 1 * 1024 * 1024;

    /// <summary>Block size for the random passes (default 4 KiB).</summary>
    public int RandomBlockBytes { get; init; } = 4096;

    /// <summary>Number of random 4 KiB operations per pass (default 2000).</summary>
    public int RandomOperations { get; init; } = 2000;

    /// <summary>Free space that must remain after allocating the test file (default 256 MiB).</summary>
    public long FreeSpaceMarginBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>The default option set.</summary>
    public static DiskBenchmarkOptions Default => new();
}

/// <summary>
/// Lowest-level disk-benchmark seam: runs a real, bounded synthetic benchmark against a single
/// drive and returns its throughput/IOPS. Kept tiny and mockable so the comparison/headline math
/// in <see cref="DevDriveCore.Services.ISpeedTestService"/> is unit-testable without touching a disk.
/// </summary>
/// <remarks>
/// The real implementation creates its temporary file under a benchmark folder on the target drive
/// and ALWAYS deletes it (and the folder, if it created it) in a <c>finally</c> block. These
/// temporary files are the only disk writes the application performs.
/// </remarks>
public interface IDiskBenchmark
{
    /// <summary>
    /// Runs the benchmark against <paramref name="driveRoot"/> (e.g. <c>G:\</c>). Throws
    /// <see cref="InvalidOperationException"/> when there is insufficient free space, and surfaces
    /// platform failures as exceptions so the service can degrade gracefully.
    /// </summary>
    DiskBenchmarkResult Run(string driveRoot, DiskBenchmarkOptions options, CancellationToken cancellationToken = default);
}
