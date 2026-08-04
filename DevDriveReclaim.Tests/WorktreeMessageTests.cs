using DevDriveReclaim;
using DevDriveReclaim.Providers;

namespace DevDriveReclaim.Tests;

/// <summary>
/// Locks the worktree grading messages against contradicting themselves. A real scan produced a row
/// reading "risk: Careful — has commits that are not on any remote" next to "detail: 0 unpushed
/// commits", which invites the user to conclude the risk grade is broken and start ignoring it.
/// </summary>
[TestClass]
public sealed class WorktreeMessageTests
{
    private static (ReclaimRisk Risk, string Reason, string Detail) Grade(WorktreeState state) =>
        WorktreeReclaimProvider.GradeForTest(state, "fallback-name");

    [TestMethod]
    public void NeverPushedBranchSaysNoUpstreamNotZeroCommits()
    {
        var state = new WorktreeState(
            "crutkas-ubiquitous-garbanzo",
            HasUncommittedChanges: false,
            ChangedFileCount: 0,
            HasUnpushedCommits: true,
            UnpushedCommitCount: 0,
            IsMerged: false,
            HasUpstream: false);

        (ReclaimRisk risk, string reason, string detail) = Grade(state);

        Assert.AreEqual(ReclaimRisk.Careful, risk,
            "No remote copy exists, so caution is still correct — only the wording was wrong.");
        StringAssert.Contains(detail, "no upstream");
        Assert.IsFalse(detail.Contains("0 unpushed", StringComparison.OrdinalIgnoreCase),
            "Reporting a zero count as the justification is what made the row read like a bug.");
        StringAssert.Contains(reason, "never been pushed");
    }

    [TestMethod]
    public void AheadOfUpstreamStillReportsTheCommitCount()
    {
        var state = new WorktreeState(
            "feature/x",
            HasUncommittedChanges: false,
            ChangedFileCount: 0,
            HasUnpushedCommits: true,
            UnpushedCommitCount: 3,
            IsMerged: false,
            HasUpstream: true);

        (ReclaimRisk risk, _, string detail) = Grade(state);

        Assert.AreEqual(ReclaimRisk.Careful, risk);
        StringAssert.Contains(detail, "3 unpushed commit");
    }

    [TestMethod]
    public void RecoveryHintDoesNotPromiseGitCanRestoreUnpushedWork()
    {
        var state = new WorktreeState(
            "solo-branch",
            HasUncommittedChanges: false,
            ChangedFileCount: 0,
            HasUnpushedCommits: true,
            UnpushedCommitCount: 0,
            IsMerged: false,
            HasUpstream: false);

        string hint = WorktreeReclaimProvider.RecoveryHintForTest(state, "solo-branch");

        Assert.IsFalse(hint.StartsWith("git worktree add", StringComparison.OrdinalIgnoreCase),
            "git worktree add only restores what a remote already has; saying so for never-pushed " +
            "work is precisely the false reassurance the recovery hint exists to prevent.");
        StringAssert.Contains(hint, "push");
    }

    [TestMethod]
    public void MergedAndCleanIsTheOnlySafeWorktree()
    {
        var merged = new WorktreeState("done", false, 0, false, 0, IsMerged: true);
        var notMerged = new WorktreeState("wip", false, 0, false, 0, IsMerged: false);

        Assert.AreEqual(ReclaimRisk.Safe, Grade(merged).Risk);
        Assert.AreEqual(ReclaimRisk.Check, Grade(notMerged).Risk,
            "Pushed but unmerged loses nothing, yet still deserves a look before deletion.");
    }

    [TestMethod]
    public void UnknownStateGradesCareful()
    {
        Assert.AreEqual(ReclaimRisk.Careful, Grade(WorktreeState.Unknown).Risk,
            "Being unable to check is a reason for caution, never permission.");
    }
}
