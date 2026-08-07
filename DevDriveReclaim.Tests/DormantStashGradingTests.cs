using DevDriveReclaim;
using DevDriveReclaim.Providers;

namespace DevDriveReclaim.Tests;

/// <summary>
/// A stash is the one piece of committed-looking work that no remote ever receives.
/// <c>refs/stash</c> sits outside <c>refs/heads</c> and <c>refs/tags</c>, so no ordinary push
/// carries it and a fresh clone arrives with none — verified against real git by pushing a
/// stashed repository and cloning it back.
/// <para>
/// That makes it a genuine loss path for a whole clone, and <b>not</b> one for a worktree: a
/// worktree shares <c>refs/stash</c> and the object database with its parent repository, so the
/// stash is still there after the worktree folder is deleted. Also verified directly.
/// </para>
/// </summary>
[TestClass]
public sealed class DormantStashGradingTests
{
    private sealed class StashingInspector(WorktreeState state, int stashes) : IWorktreeInspector
    {
        public int StashCallCount { get; private set; }

        public WorktreeState Inspect(string worktreePath, CancellationToken cancellationToken) => state;

        public int StashCount(string repositoryPath, CancellationToken cancellationToken)
        {
            StashCallCount++;
            return stashes;
        }
    }

    private static WorktreeState CleanAndPushed =>
        new("main", HasUncommittedChanges: false, ChangedFileCount: 0,
            HasUnpushedCommits: false, UnpushedCommitCount: 0, IsMerged: true);

    private static ReclaimCandidate? ScanOne(IWorktreeInspector inspector, ReclaimFixture fixture)
    {
        var context = new ReclaimScanContext([@"C:\"], [fixture.Root], minimumCandidateBytes: 1);

        return new DormantProjectReclaimProvider(inspector)
            .ScanAsync(context, null, CancellationToken.None)
            .GetAwaiter().GetResult()
            .FirstOrDefault();
    }

    private static void Dormant(ReclaimFixture fixture, string name)
    {
        string repo = fixture.Repo(name);
        fixture.File(Path.Combine(name, "src", "a.bin"), 80L * 1024 * 1024);
        fixture.Age(repo, TimeSpan.FromDays(900));
    }

    [TestMethod]
    public void AStashedDormantRepositoryIsNotOfferedAsFullyRecoverable()
    {
        using var fixture = new ReclaimFixture();
        Dormant(fixture, "old-project");

        ReclaimCandidate? found = ScanOne(new StashingInspector(CleanAndPushed, 2), fixture);

        Assert.IsNotNull(found);
        Assert.AreEqual(ReclaimRisk.Careful, found.Risk);
        Assert.IsFalse(
            found.Reason.Contains("a clone gets it all back", StringComparison.OrdinalIgnoreCase),
            "A clone brings back no stashes at all, so this is the sentence that had to change.");
        StringAssert.Contains(found.Detail!, "2 stashes");
        StringAssert.Contains(found.RecoveryHint, "git stash pop");
    }

    [TestMethod]
    public void AnUnstashedDormantRepositoryIsStillOfferedAsRecoverable()
    {
        using var fixture = new ReclaimFixture();
        Dormant(fixture, "old-project");

        // The counter-test: the guard above must not have been bought by grading every dormant
        // repository Careful, which would make the whole category unusable.
        ReclaimCandidate? found = ScanOne(new StashingInspector(CleanAndPushed, 0), fixture);

        Assert.IsNotNull(found);
        Assert.AreEqual(ReclaimRisk.Check, found.Risk);
        StringAssert.Contains(found.Reason, "a clone gets it all back");
    }

    [TestMethod]
    public void TheStashIsNotLookedUpWhenItCannotChangeTheAnswer()
    {
        using var fixture = new ReclaimFixture();
        Dormant(fixture, "old-project");

        // A repository with uncommitted work is already Careful, and the lookup costs a git
        // invocation each time -- measured at 77 ms against this machine's repositories.
        var dirty = new WorktreeState("main", HasUncommittedChanges: true, ChangedFileCount: 3,
            HasUnpushedCommits: false, UnpushedCommitCount: 0, IsMerged: false);

        var inspector = new StashingInspector(dirty, 5);
        ReclaimCandidate? found = ScanOne(inspector, fixture);

        Assert.IsNotNull(found);
        Assert.AreEqual(ReclaimRisk.Careful, found.Risk);
        Assert.AreEqual(0, inspector.StashCallCount);
    }
}
