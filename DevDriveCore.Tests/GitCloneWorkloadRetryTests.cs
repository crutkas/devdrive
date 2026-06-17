using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;
using NSubstitute;

namespace DevDriveCore.Tests;

/// <summary>
/// Verifies the git benchmark is resilient to a single transient clone failure. A many-small-file
/// checkout can spuriously exit non-zero when synchronous Defender scanning (performance mode off)
/// briefly locks a freshly written file on the system drive; the workload retries a bounded number of
/// times before honestly skipping. The process runner is faked, so no real <c>git</c> is launched and
/// no fixture is generated — only a throwaway working folder is created under a temp "drive root".
/// </summary>
[TestClass]
public sealed class GitCloneWorkloadRetryTests
{
    private static ProcessRunResult Ok() => new(0, string.Empty, string.Empty);

    // The observed signature of the transient failure: a non-zero exit with no captured diagnostics.
    private static ProcessRunResult TransientFailure() => new(1, string.Empty, string.Empty);

    [TestMethod]
    public void MeasureOnce_RetriesTransientCloneFailure_ThenSucceeds()
    {
        var runner = Substitute.For<IWorkloadProcessRunner>();
        runner.Run(Arg.Any<WorkloadProcessRequest>(), Arg.Any<CancellationToken>())
            .Returns(TransientFailure(), Ok()); // first clone hiccups, the retry succeeds

        var git = new GitCloneWorkload(runner) { Profile = WorkloadProfile.Thorough };
        string driveRoot = CreateTempRoot();
        try
        {
            double seconds = git.MeasureOnce(driveRoot, CancellationToken.None);

            Assert.IsGreaterThanOrEqualTo(0.0, seconds, "a successful retry should still yield a real timing");
            runner.Received(2).Run(Arg.Any<WorkloadProcessRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            git.Cleanup();
            DeleteTempRoot(driveRoot);
        }
    }

    [TestMethod]
    public void MeasureOnce_WhenEveryAttemptFails_SkipsWithReason()
    {
        var runner = Substitute.For<IWorkloadProcessRunner>();
        runner.Run(Arg.Any<WorkloadProcessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessRunResult(1, string.Empty, "fatal: unable to write file"));

        var git = new GitCloneWorkload(runner) { Profile = WorkloadProfile.Thorough };
        string driveRoot = CreateTempRoot();
        try
        {
            // A persistent failure is not transient — it is surfaced as a skip (the orchestrator turns
            // WorkloadUnavailableException into a "SKIPPED — reason" row), after a bounded set of attempts.
            Assert.ThrowsExactly<WorkloadUnavailableException>(
                () => git.MeasureOnce(driveRoot, CancellationToken.None));
            runner.Received(3).Run(Arg.Any<WorkloadProcessRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            git.Cleanup();
            DeleteTempRoot(driveRoot);
        }
    }

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "git-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of a temp fixture dir.
        }
    }
}
