using System.ComponentModel;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;

namespace DevDriveCore.Tests;

/// <summary>
/// Guarded, NON-mocked test for the real <see cref="DiskBenchmark"/> P/Invoke path. It runs a tiny
/// (16 MiB) benchmark inside a throwaway temp folder, asserts the four metrics come back positive,
/// and verifies the only side effect — the temporary file — is cleaned up. Categorised
/// <c>Integration</c> so it can be excluded in environments where unbuffered I/O is unavailable;
/// it goes <see cref="Assert.Inconclusive(string)"/> rather than failing in that case.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class DiskBenchmarkIntegrationTests
{
    [TestMethod]
    public void Run_RealBenchmark_ProducesPositiveMetricsAndCleansUp()
    {
        // Hermetic root: the benchmark creates "DevDriveManagerBench" UNDER this folder.
        string root = Path.Combine(Path.GetTempPath(), "ddm-bench-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DiskBenchmarkOptions
        {
            FileSizeBytes = 16L * 1024 * 1024, // small + fast
            RandomOperations = 200,
        };

        try
        {
            DiskBenchmarkResult result = new DiskBenchmark().Run(root, options);

            Assert.IsGreaterThan(0d, result.SequentialWriteMBps, "Sequential write MB/s should be positive.");
            Assert.IsGreaterThan(0d, result.SequentialReadMBps, "Sequential read MB/s should be positive.");
            Assert.IsGreaterThan(0d, result.RandomRead4KIops, "Random 4K read IOPS should be positive.");
            Assert.IsGreaterThan(0d, result.RandomWrite4KIops, "Random 4K write IOPS should be positive.");

            // The only side effect is the temp file, which must be gone (deleted in finally).
            string benchFolder = Path.Combine(root, "DevDriveManagerBench");
            if (Directory.Exists(benchFolder))
            {
                Assert.IsEmpty(
                    Directory.EnumerateFiles(benchFolder, "ddm-bench-*.tmp"),
                    "The benchmark must delete its temporary file in the finally block.");
            }
        }
        catch (InvalidOperationException ex)
        {
            Assert.Inconclusive($"Not enough free space to run the disk benchmark: {ex.Message}");
        }
        catch (Win32Exception ex)
        {
            Assert.Inconclusive($"Unbuffered I/O unavailable on this volume (Win32): {ex.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of the throwaway root.
            }
        }
    }

    [TestMethod]
    public void Run_InsufficientFreeSpace_Throws()
    {
        string root = Path.Combine(Path.GetTempPath(), "ddm-bench-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // Demand an impossible amount of free space so the guard trips deterministically.
        var options = new DiskBenchmarkOptions
        {
            FileSizeBytes = 16L * 1024 * 1024,
            FreeSpaceMarginBytes = long.MaxValue / 2,
        };

        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => new DiskBenchmark().Run(root, options));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
