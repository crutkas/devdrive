using DevDriveReclaim;
using DevDriveReclaim.Providers;

namespace DevDriveReclaim.Tests;

/// <summary>
/// The hole this closes: <c>git status --porcelain</c> is silent about ignored files, so a
/// worktree that is clean, pushed and merged — but is also the only place a <c>.env</c> exists —
/// graded <see cref="ReclaimRisk.Safe"/>, and nothing anywhere in the room mentioned the file.
/// </summary>
/// <remarks>
/// This mattered more when a scan pre-ticked the whole Safe tier; selection is now empty until the
/// user makes it, so a bad grade no longer arrives pre-armed. The grade still has to be right —
/// "Safe" is the word this room uses to earn a tick, and it should not be given to a folder that
/// holds the only copy of something.
/// </remarks>
[TestClass]
public sealed class WorktreeLocalOnlyGradingTests
{
    private static WorktreeState CleanAndMerged(params string[] localOnly) => new(
        "feature/done",
        HasUncommittedChanges: false,
        ChangedFileCount: 0,
        HasUnpushedCommits: false,
        UnpushedCommitCount: 0,
        IsMerged: true,
        HasUpstream: true,
        LocalOnlyIgnoredFiles: localOnly);

    [TestMethod]
    public void AMergedWorktreeCarryingAnEnvFileIsNoLongerSafe()
    {
        (ReclaimRisk risk, string reason, string detail) =
            WorktreeReclaimProvider.GradeForTest(CleanAndMerged(".env"), "w");

        Assert.AreEqual(ReclaimRisk.Careful, risk);
        StringAssert.Contains(detail, ".env");

        // The reason has to hold both halves at once, or it reads as a contradiction of the row
        // above it: the tracked code really is recoverable, and that is not the point.
        StringAssert.Contains(reason, "ignores");
    }

    [TestMethod]
    public void TheSameWorktreeWithoutIgnoredSecretsStaysSafe()
    {
        (ReclaimRisk risk, _, _) = WorktreeReclaimProvider.GradeForTest(CleanAndMerged(), "w");

        // The other half of the contract. A rule that grades everything Careful protects nobody.
        Assert.AreEqual(ReclaimRisk.Safe, risk);
    }

    [TestMethod]
    public void ACleanButUnmergedWorktreeIsAlsoLiftedOutOfTheSelectableTiers()
    {
        WorktreeState state = CleanAndMerged("certs/dev.pfx") with { IsMerged = false };

        (ReclaimRisk risk, _, _) = WorktreeReclaimProvider.GradeForTest(state, "w");

        Assert.AreEqual(ReclaimRisk.Careful, risk);
    }

    [TestMethod]
    public void ManyIgnoredSecretsAreSummarisedRatherThanListed()
    {
        (_, _, string detail) = WorktreeReclaimProvider.GradeForTest(
            CleanAndMerged(".env", "certs/dev.pfx", "secrets.json"), "w");

        StringAssert.Contains(detail, ".env");
        StringAssert.Contains(detail, "2 more");
    }

    [TestMethod]
    public void UncommittedChangesStillWinTheMessage()
    {
        WorktreeState state = CleanAndMerged(".env") with
        {
            HasUncommittedChanges = true,
            ChangedFileCount = 3,
        };

        (ReclaimRisk risk, _, string detail) = WorktreeReclaimProvider.GradeForTest(state, "w");

        // Both are Careful, so the grade is unaffected; the detail should name the stronger and
        // more immediately actionable signal rather than the subtler one.
        Assert.AreEqual(ReclaimRisk.Careful, risk);
        StringAssert.Contains(detail, "uncommitted");
    }

    [TestMethod]
    public void TheRecoveryHintRefusesToPromiseTheIgnoredFilesBack()
    {
        string hint = WorktreeReclaimProvider.RecoveryHintForTest(CleanAndMerged(".env"), "w");

        // "git worktree add ..." on its own reads as full recovery, which for these files is false.
        StringAssert.Contains(hint, ".env");
        StringAssert.Contains(hint, "not");
    }

    [TestMethod]
    public void AWorktreeStateDefaultsToCarryingNoIgnoredSecrets()
    {
        var state = new WorktreeState("b", false, 0, false, 0, true);

        // Every existing call site omits the new argument, so the default decides whether this
        // change quietly regrades the whole room.
        Assert.IsFalse(state.HasLocalOnlyIgnoredFiles);
        Assert.IsEmpty(state.LocalOnlyIgnoredFiles);
        Assert.AreEqual(ReclaimRisk.Safe, WorktreeReclaimProvider.GradeForTest(state, "w").Risk);
    }
}
