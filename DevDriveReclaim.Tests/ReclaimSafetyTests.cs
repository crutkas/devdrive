using DevDriveReclaim.Providers;

namespace DevDriveReclaim.Tests;

/// <summary>
/// The safety invariants. These are the tests that matter most: everything else here is a feature,
/// but a break in any of these means the tool can destroy work the user cannot get back.
/// </summary>
[TestClass]
public sealed class ReclaimSafetyTests
{
    [TestMethod]
    public void NothingRiskierThanSafeIsEverPreselected()
    {
        var result = new ReclaimResult(
        [
            new ReclaimCategoryResult(BuildOutputReclaimProvider.ReclaimCategory,
            [
                Candidate("a", 100, ReclaimRisk.Safe),
                Candidate("b", 900, ReclaimRisk.Check),
                Candidate("c", 500, ReclaimRisk.Careful),
            ]),
        ], DateTimeOffset.UtcNow);

        Assert.HasCount(1, result.DefaultSelection);
        Assert.AreEqual(ReclaimRisk.Safe, result.DefaultSelection[0].Risk);

        // Stated as a direct assertion too: the bug this guards against is a future refactor that
        // preselects "everything under a size threshold" or "everything the scan is confident about".
        Assert.IsFalse(result.DefaultSelection.Any(c => c.Risk != ReclaimRisk.Safe));
    }

    [TestMethod]
    public void EveryCandidateMustCarryARecoveryHint()
    {
        // A delete tool that cannot say how to undo a deletion has no business offering it, so the
        // constructor refuses to build a candidate without one.
        Assert.ThrowsExactly<ArgumentException>(() => new ReclaimCandidate(
            "cat", @"C:\x", "x", 1, ReclaimRisk.Safe, "reason", recoveryHint: "   "));
    }

    [TestMethod]
    public void ADirtyWorktreeIsCarefulNoMatterHowLargeOrOld()
    {
        using var fixture = new ReclaimFixture();
        fixture.Worktree("feature-x", @"C:\repo\.git");
        fixture.File(@"feature-x\big.bin", 200L * 1024 * 1024);

        var provider = new WorktreeReclaimProvider(new StubWorktreeInspector(
            new WorktreeState("feature-x", HasUncommittedChanges: true, ChangedFileCount: 3,
                HasUnpushedCommits: false, UnpushedCommitCount: 0, IsMerged: true)));

        IReadOnlyList<ReclaimCandidate> found = provider
            .ScanAsync(Context(fixture), null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.HasCount(1, found);
        Assert.AreEqual(ReclaimRisk.Careful, found[0].Risk);
        Assert.Contains("uncommitted", found[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void AWorktreeWithUnpushedCommitsIsCarefulEvenWhenClean()
    {
        using var fixture = new ReclaimFixture();
        fixture.Worktree("feature-y", @"C:\repo\.git");
        fixture.File(@"feature-y\big.bin", 8L * 1024 * 1024);

        var provider = new WorktreeReclaimProvider(new StubWorktreeInspector(
            new WorktreeState("feature-y", HasUncommittedChanges: false, ChangedFileCount: 0,
                HasUnpushedCommits: true, UnpushedCommitCount: 2, IsMerged: false)));

        IReadOnlyList<ReclaimCandidate> found = provider
            .ScanAsync(Context(fixture), null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.HasCount(1, found);
        Assert.AreEqual(ReclaimRisk.Careful, found[0].Risk);
    }

    [TestMethod]
    public void AMergedCleanWorktreeIsSafe()
    {
        using var fixture = new ReclaimFixture();
        fixture.Worktree("done", @"C:\repo\.git");
        fixture.File(@"done\big.bin", 8L * 1024 * 1024);

        var provider = new WorktreeReclaimProvider(new StubWorktreeInspector(
            new WorktreeState("done", HasUncommittedChanges: false, ChangedFileCount: 0,
                HasUnpushedCommits: false, UnpushedCommitCount: 0, IsMerged: true)));

        IReadOnlyList<ReclaimCandidate> found = provider
            .ScanAsync(Context(fixture), null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.HasCount(1, found);
        Assert.AreEqual(ReclaimRisk.Safe, found[0].Risk);
    }

    [TestMethod]
    public void AnUninspectableWorktreeDefaultsToCareful()
    {
        // Being unable to check is a reason for caution, never permission.
        WorktreeState unknown = WorktreeState.Unknown;
        Assert.IsTrue(unknown.HasUncommittedChanges);
        Assert.IsTrue(unknown.HasUnpushedCommits);
        Assert.IsFalse(unknown.IsMerged);
    }

    [TestMethod]
    public void ADormantProjectIsNeverGradedSafe()
    {
        using var fixture = new ReclaimFixture();
        string repo = fixture.Repo("old-thing");
        fixture.File(@"old-thing\src\a.bin", 80L * 1024 * 1024);
        fixture.Age(repo, TimeSpan.FromDays(900));

        var provider = new DormantProjectReclaimProvider(new StubWorktreeInspector(
            new WorktreeState("main", HasUncommittedChanges: false, ChangedFileCount: 0,
                HasUnpushedCommits: false, UnpushedCommitCount: 0, IsMerged: true)));

        IReadOnlyList<ReclaimCandidate> found = provider
            .ScanAsync(Context(fixture), null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.HasCount(1, found);
        Assert.AreNotEqual(ReclaimRisk.Safe, found[0].Risk);
    }

    [TestMethod]
    public void AnActiveProjectIsNotReportedAsDormant()
    {
        using var fixture = new ReclaimFixture();
        fixture.Repo("current");
        fixture.File(@"current\src\a.bin", 80L * 1024 * 1024);

        var provider = new DormantProjectReclaimProvider(new StubWorktreeInspector(
            new WorktreeState("main", false, 0, false, 0, true)));

        IReadOnlyList<ReclaimCandidate> found = provider
            .ScanAsync(Context(fixture), null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.IsEmpty(found);
    }

    [TestMethod]
    public void AnOrphanedWorktreeFolderIsCarefulBecauseNothingCanBeChecked()
    {
        using var fixture = new ReclaimFixture();
        fixture.Worktree(@"lot\wt-a", @"C:\repo\.git\worktrees\a");
        fixture.Worktree(@"lot\wt-b", @"C:\repo\.git\worktrees\b");
        fixture.File(@"lot\leftover\build.bin", 90L * 1024 * 1024);

        var provider = new WorktreeReclaimProvider(new StubWorktreeInspector(
            new WorktreeState("main", false, 0, false, 0, IsMerged: true)));

        IReadOnlyList<ReclaimCandidate> found = provider
            .ScanAsync(Context(fixture), null, CancellationToken.None).GetAwaiter().GetResult();

        ReclaimCandidate orphan = found.Single(c => c.DisplayName == "leftover");

        // The stub grades every real worktree Safe. The orphan must not inherit that: it was never
        // inspected, because there is no .git to inspect.
        Assert.AreEqual(ReclaimRisk.Careful, orphan.Risk);
        Assert.Contains("no .git", orphan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("beside 2 worktrees", orphan.Detail ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void AnEmptyLeftoverFolderIsClutterNotReclaimableSpace()
    {
        using var fixture = new ReclaimFixture();
        fixture.Worktree(@"lot\wt-a", @"C:\repo\.git\worktrees\a");
        fixture.Worktree(@"lot\wt-b", @"C:\repo\.git\worktrees\b");
        fixture.Dir(@"lot\empty-leftover");

        var provider = new WorktreeReclaimProvider(new StubWorktreeInspector(
            new WorktreeState("main", false, 0, false, 0, IsMerged: true)));

        var context = new ReclaimScanContext(
            [@"C:\"], [fixture.Root], minimumCandidateBytes: 64L * 1024 * 1024);

        IReadOnlyList<ReclaimCandidate> found = provider
            .ScanAsync(context, null, CancellationToken.None).GetAwaiter().GetResult();

        // Most orphans are empty directories git left behind. They return no space, and a room
        // about reclaiming space listing eleven 0-byte rows is a chore, not a decision.
        Assert.IsEmpty(found.Where(c => c.DisplayName == "empty-leftover"));
    }

    private static ReclaimScanContext Context(ReclaimFixture fixture) =>
        new([@"C:\"], [fixture.Root], minimumCandidateBytes: 1);

    private static ReclaimCandidate Candidate(string name, long size, ReclaimRisk risk) =>
        new("cat", $@"C:\test\{name}", name, size, risk, "reason", "recovery");
}
