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

        // Measured, not assumed: a worktree's branch ref and objects live in the parent repository,
        // so deleting the folder leaves both. Grading this Careful fired on 29 of 54 worktrees on a
        // real machine, and a warning that fires on the majority teaches people to type past it.
        Assert.AreEqual(ReclaimRisk.Check, risk,
            "The branch outlives the folder, so this is worth a look rather than a DELETE prompt.");
        StringAssert.Contains(detail, "no upstream");
        Assert.IsFalse(detail.Contains("0 unpushed", StringComparison.OrdinalIgnoreCase),
            "Reporting a zero count as the justification is what made the row read like a bug.");
        StringAssert.Contains(reason, "parent repository");
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

        Assert.AreEqual(ReclaimRisk.Check, risk);
        StringAssert.Contains(detail, "3 unpushed commit");
    }

    [TestMethod]
    public void ADetachedWorktreeWithCommitsIsTheOneThatStaysCareful()
    {
        var state = new WorktreeState(
            Branch: null,
            HasUncommittedChanges: false,
            ChangedFileCount: 0,
            HasUnpushedCommits: true,
            UnpushedCommitCount: 2,
            IsMerged: false,
            HasUpstream: false,
            IsDetached: true);

        (ReclaimRisk risk, string reason, string detail) = Grade(state);

        // No branch ref names these commits, so once the folder and its administrative entry are
        // gone they are unreachable and git collects them. Verified by doing exactly that.
        Assert.AreEqual(ReclaimRisk.Careful, risk);
        StringAssert.Contains(detail, "detached HEAD");
        StringAssert.Contains(reason, "unreachable");
    }

    [TestMethod]
    public void IrreplaceableIgnoredFilesOutrankTheUnpushedVerdict()
    {
        // Both conditions at once. Unpushed is now merely Check, so if it were tested first it
        // would return before the Careful that the ignored files earn.
        var state = new WorktreeState(
            "feature/y",
            HasUncommittedChanges: false,
            ChangedFileCount: 0,
            HasUnpushedCommits: true,
            UnpushedCommitCount: 4,
            IsMerged: false,
            HasUpstream: true,
            LocalOnlyIgnoredFiles: [".env"]);

        (ReclaimRisk risk, _, string detail) = Grade(state);

        Assert.AreEqual(ReclaimRisk.Careful, risk,
            "A Check verdict reached first would mask the one thing here that cannot be recovered.");
        StringAssert.Contains(detail, ".env");
    }

    [TestMethod]
    public void RecoveryHintDoesNotPromiseGitCanRestoreUnpushedWork()
    {
        var state = new WorktreeState(
            Branch: null,
            HasUncommittedChanges: false,
            ChangedFileCount: 0,
            HasUnpushedCommits: true,
            UnpushedCommitCount: 2,
            IsMerged: false,
            HasUpstream: false,
            IsDetached: true);

        string hint = WorktreeReclaimProvider.RecoveryHintForTest(state, "solo-branch");

        Assert.IsFalse(hint.StartsWith("git worktree add", StringComparison.OrdinalIgnoreCase),
            "Commits on no branch are not restored by re-adding the worktree; they are simply gone.");
        StringAssert.Contains(hint, "git branch");
    }

    [TestMethod]
    public void RecoveryHintClearsTheStaleWorktreeEntryFirst()
    {
        var state = new WorktreeState("feature/z", false, 0, false, 0, IsMerged: true);

        string hint = WorktreeReclaimProvider.RecoveryHintForTest(state, "feature-z");

        // The parent repository keeps an administrative entry for a worktree whose folder has gone,
        // and git refuses to re-add at that path until it is cleared. A hint that fails when
        // followed is worse than none.
        StringAssert.Contains(hint, "git worktree prune");
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
