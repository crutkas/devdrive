using DevDriveCore.Abstractions;
using DevDriveCore.Platform;

namespace DevDriveCore.Tests;

/// <summary>
/// Guarded, NON-mocked test for the real <see cref="GitCloneWorkload"/> — the always-available
/// benchmark. It seeds a tiny bare repo and times a single real <c>git clone</c> inside a throwaway
/// temp folder, asserts the time is positive and the per-iteration work dir is gone, then asserts
/// <see cref="WorkloadBenchmarkBase.Cleanup"/> removes every fixture. Categorised <c>Integration</c>;
/// goes <see cref="Assert.Inconclusive(string)"/> (rather than failing) when git is unavailable or
/// the volume can't host the run.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class GitCloneWorkloadIntegrationTests
{
    // Mirrors the internal WorkloadPaths.BenchFolderName (not visible to the test assembly).
    private const string BenchFolderName = "DevDriveManagerWorkloadBench";

    [TestMethod]
    public void MeasureOnce_RealClone_ProducesPositiveTimeAndCleansUp()
    {
        string root = Path.Combine(Path.GetTempPath(), "ddm-wl-git-" + Guid.NewGuid().ToString("N"));
        string seedRoot = Path.Combine(root, "seed");
        Directory.CreateDirectory(root);

        // Same throwaway folder stands in for both "drives": the test only needs one real clone.
        var environment = new WorkloadEnvironment(root, root, seedRoot);
        var benchmark = new GitCloneWorkload(new WorkloadProcessRunner());

        try
        {
            benchmark.Prepare(environment, CancellationToken.None);
            double seconds = benchmark.MeasureOnce(root, CancellationToken.None);

            Assert.IsGreaterThan(0d, seconds, "A successful clone must take a positive amount of time.");

            // The per-iteration working directory must be deleted as soon as the measurement ends.
            string benchRoot = Path.Combine(root, BenchFolderName, "git-clone");
            if (Directory.Exists(benchRoot))
            {
                Assert.IsEmpty(
                    Directory.EnumerateDirectories(benchRoot, "run-*"),
                    "MeasureOnce must delete its per-iteration work directory in the finally block.");
            }

            // Cleanup (the method under safety test) must remove every fixture it created.
            benchmark.Cleanup();
            Assert.IsFalse(
                Directory.Exists(Path.Combine(root, BenchFolderName)),
                "Cleanup must remove the per-drive bench folder.");
            Assert.IsFalse(Directory.Exists(seedRoot), "Cleanup must remove the seeded fixtures.");
        }
        catch (WorkloadUnavailableException ex)
        {
            Assert.Inconclusive($"git clone benchmark unavailable on this machine: {ex.Message}");
        }
        finally
        {
            benchmark.Cleanup();
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
}
