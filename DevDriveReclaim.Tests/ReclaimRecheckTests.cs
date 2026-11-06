using DevDriveReclaim.Providers;

namespace DevDriveReclaim.Tests;

/// <summary>
/// A grade read from git is read again before the row goes (Dev Config #341). A worktree graded Safe at the scan and edited
/// before the run holds the only copy of that edit; the run used to remove it on the strength of the scan's answer.
/// </summary>
/// <remarks>
/// Every removal here is permanent (<see cref="ReclaimCandidate.WithoutRecycleBin"/>) and inside a <see cref="ReclaimFixture"/>,
/// so nothing reaches the developer's Recycle Bin.
/// </remarks>
[TestClass]
public sealed class ReclaimRecheckTests
{
    private static readonly WorktreeState CleanMergedPushed = new(
        "feature", HasUncommittedChanges: false, ChangedFileCount: 0, HasUnpushedCommits: false, UnpushedCommitCount: 0,
        IsMerged: true, Head: "1111111111111111111111111111111111111111");

    /// <summary>Answers whatever the test last set, as git would after someone worked in the tree.</summary>
    private sealed class ScriptedInspector(WorktreeState state, int stashes = 0) : IWorktreeInspector
    {
        public WorktreeState State { get; set; } = state;

        public int Stashes { get; set; } = stashes;

        public int Calls { get; private set; }

        public WorktreeState Inspect(string worktreePath, CancellationToken cancellationToken)
        {
            Calls++;
            return State;
        }

        public int StashCount(string repositoryPath, CancellationToken cancellationToken) => Stashes;
    }

    private static Task<ReclaimOutcome> Run(IWorktreeInspector git, params ReclaimCandidate[] selection) =>
        new ReclaimExecutor(_ => null, Providers.Models.LocalModelRemoval.Default, git)
            .ExecuteAsync([.. selection.Select(c => c.WithoutRecycleBin())], null, CancellationToken.None);

    private static ReclaimCandidate Graded(string path, WorktreeState state, int? stashes = null, string category = "worktrees") =>
        new(category, path, Path.GetFileName(path), 4096, ReclaimRisk.Safe, "graded from git", "git worktree add")
        {
            GitEvidence = new GitEvidence(state, stashes),
        };

    /// <summary>
    /// A clean, merged and pushed worktree on a real repository: an <c>origin</c> it was pushed to, and a worktree on a branch
    /// that tracks <c>origin/main</c>. Returns the worktree's folder.
    /// </summary>
    private static string MergedWorktree(ReclaimFixture fixture)
    {
        string root = fixture.Root;
        TestGit.Run(root, "init --bare --initial-branch=main origin.git");
        TestGit.Run(root, "clone origin.git main");
        string main = Path.Combine(root, "main");
        TestGit.Identity(main);
        File.WriteAllText(Path.Combine(main, "README.md"), "hello\n");
        File.WriteAllBytes(Path.Combine(main, "data.bin"), new byte[64 * 1024]);
        TestGit.Run(main, "add .");
        TestGit.Run(main, "commit -m init");
        TestGit.Run(main, "push origin main");

        string worktree = Path.Combine(root, "wt");
        TestGit.Run(main, $"worktree add -b feature \"{worktree}\" origin/main");
        TestGit.Run(worktree, "branch --set-upstream-to=origin/main");
        return worktree;
    }

    private static async Task<ReclaimCandidate> ScanWorktree(ReclaimFixture fixture, string worktree)
    {
        IReadOnlyList<ReclaimCandidate> found = await new WorktreeReclaimProvider().ScanAsync(
            new ReclaimScanContext([Path.GetPathRoot(fixture.Root)!], [fixture.Root], minimumCandidateBytes: 0),
            null,
            CancellationToken.None);

        return found.Single(c => string.Equals(c.Path.TrimEnd('\\'), worktree, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ASafeWorktreeEditedAfterTheScanIsKeptByTheRun()
    {
        if (!TestGit.IsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string worktree = MergedWorktree(fixture);
        ReclaimCandidate row = await ScanWorktree(fixture, worktree);
        Assert.AreEqual(ReclaimRisk.Safe, row.Risk, "the setup is a worktree the scan grades Safe");

        // After the scan, real work lands in it and isn't committed.
        string work = Path.Combine(worktree, "work-in-progress.cs");
        File.WriteAllText(work, "// two hours of uncommitted work\n");

        ReclaimOutcome outcome = await new ReclaimExecutor().ExecuteAsync([row.WithoutRecycleBin()], null, CancellationToken.None);

        ReclaimItemOutcome item = outcome.Items.Single();
        Assert.IsTrue(File.Exists(work), "the edit made after the scan was deleted");
        Assert.AreEqual(ReclaimItemStatus.Failed, item.Status);
        Assert.AreEqual(ReclaimChange.UncommittedChanges, item.Change);
        StringAssert.StartsWith(item.Message, "kept — changed since the scan: it has uncommitted changes now");
        Assert.AreEqual(0, outcome.BytesFreed);
        Assert.AreEqual(1, outcome.FailedCount, "a kept row needs attention: the user asked for it and didn't get it");
    }

    [TestMethod]
    public async Task AnUnchangedSafeWorktreeIsStillRemoved()
    {
        if (!TestGit.IsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string worktree = MergedWorktree(fixture);
        ReclaimCandidate row = await ScanWorktree(fixture, worktree);

        ReclaimOutcome outcome = await new ReclaimExecutor().ExecuteAsync([row.WithoutRecycleBin()], null, CancellationToken.None);

        Assert.AreEqual(ReclaimItemStatus.Deleted, outcome.Items.Single().Status, outcome.Items.Single().Message);
        Assert.IsFalse(Directory.Exists(worktree));
    }

    [TestMethod]
    public async Task ADormantCloneWithANewStashIsKept()
    {
        using var fixture = new ReclaimFixture();
        string clone = fixture.Repo("old-project");
        var git = new ScriptedInspector(CleanMergedPushed with { Branch = "main" }, stashes: 0);
        ReclaimCandidate row = Graded(clone, git.State, stashes: 0, category: "dormant-projects");

        git.Stashes = 1;
        ReclaimOutcome outcome = await Run(git, row);

        Assert.IsTrue(Directory.Exists(clone));
        Assert.AreEqual(ReclaimChange.Stashes, outcome.Items.Single().Change);
        StringAssert.Contains(outcome.Items.Single().Message, "1 stash now");
    }

    [TestMethod]
    public async Task ARowGitCanNoLongerAnswerForIsKept()
    {
        using var fixture = new ReclaimFixture();
        string worktree = fixture.Dir("wt");
        var git = new ScriptedInspector(CleanMergedPushed);
        ReclaimCandidate row = Graded(worktree, git.State);

        git.State = WorktreeState.Unknown;
        ReclaimOutcome outcome = await Run(git, row);

        Assert.IsTrue(Directory.Exists(worktree));
        Assert.AreEqual(ReclaimChange.Unknown, outcome.Items.Single().Change);
    }

    [TestMethod]
    public async Task ARowWithNothingToAskGitAboutIsNotAskedAbout()
    {
        using var fixture = new ReclaimFixture();

        // An orphaned worktree folder: there's no .git, so the scan graded it Careful without asking git, and the run has
        // nothing to compare. Asking would answer Unknown and keep it for ever.
        string orphan = fixture.Dir("leftover");
        var git = new ScriptedInspector(WorktreeState.Unknown);

        ReclaimOutcome outcome = await Run(
            git, new ReclaimCandidate("worktrees", orphan, "leftover", 4096, ReclaimRisk.Careful, "no .git", "nothing"));

        Assert.AreEqual(ReclaimItemStatus.Deleted, outcome.Items.Single().Status);
        Assert.AreEqual(0, git.Calls);
    }

    [TestMethod]
    public async Task AnUnchangedRowIsAskedAboutAndRemoved()
    {
        using var fixture = new ReclaimFixture();
        string worktree = fixture.Dir("wt");
        var git = new ScriptedInspector(CleanMergedPushed);

        ReclaimOutcome outcome = await Run(git, Graded(worktree, CleanMergedPushed));

        Assert.AreEqual(1, git.Calls);
        Assert.AreEqual(ReclaimItemStatus.Deleted, outcome.Items.Single().Status);
        Assert.AreEqual(ReclaimChange.None, outcome.Items.Single().Change);
        Assert.IsFalse(Directory.Exists(worktree));
    }

    [TestMethod]
    public void TheSameAnswerIsNoChange()
    {
        Assert.IsNull(ReclaimRecheck.Compare(new GitEvidence(CleanMergedPushed), CleanMergedPushed with { }, null));
    }

    [TestMethod]
    public void EachRiskierAnswerIsAChange()
    {
        var before = new GitEvidence(CleanMergedPushed);

        Assert.AreEqual(ReclaimChange.Branch, ReclaimRecheck.Compare(before, CleanMergedPushed with { Branch = "other" }, null)?.Change);
        Assert.AreEqual(ReclaimChange.Branch, ReclaimRecheck.Compare(before, CleanMergedPushed with { Branch = null, IsDetached = true }, null)?.Change);
        Assert.AreEqual(ReclaimChange.Head, ReclaimRecheck.Compare(before, CleanMergedPushed with { Head = new string('2', 40) }, null)?.Change);
        Assert.AreEqual(ReclaimChange.Head, ReclaimRecheck.Compare(before, CleanMergedPushed with { Head = null }, null)?.Change);
        Assert.AreEqual(
            ReclaimChange.UncommittedChanges,
            ReclaimRecheck.Compare(before, CleanMergedPushed with { HasUncommittedChanges = true, ChangedFileCount = 1 }, null)?.Change);
        Assert.AreEqual(
            ReclaimChange.NoUpstream,
            ReclaimRecheck.Compare(before, CleanMergedPushed with { HasUpstream = false, HasUnpushedCommits = true }, null)?.Change);
        Assert.AreEqual(
            ReclaimChange.UnpushedCommits,
            ReclaimRecheck.Compare(before, CleanMergedPushed with { HasUnpushedCommits = true, UnpushedCommitCount = 2 }, null)?.Change);
        Assert.AreEqual(ReclaimChange.NotMerged, ReclaimRecheck.Compare(before, CleanMergedPushed with { IsMerged = false }, null)?.Change);
        Assert.AreEqual(
            ReclaimChange.LocalOnlyFiles,
            ReclaimRecheck.Compare(before, CleanMergedPushed with { LocalOnlyIgnoredFiles = [".env"] }, null)?.Change);
        Assert.AreEqual(ReclaimChange.Unknown, ReclaimRecheck.Compare(before, WorktreeState.Unknown, null)?.Change);
        Assert.AreEqual(
            ReclaimChange.Stashes,
            ReclaimRecheck.Compare(new GitEvidence(CleanMergedPushed, StashCount: 1), CleanMergedPushed, stashesNow: 2)?.Change);
    }

    [TestMethod]
    public void MoreOfWhatTheGradeAlreadyCountedIsAChangeAndLessIsNot()
    {
        WorktreeState dirty = CleanMergedPushed with { HasUncommittedChanges = true, ChangedFileCount = 2 };
        var before = new GitEvidence(dirty);

        ReclaimChanged? more = ReclaimRecheck.Compare(before, dirty with { ChangedFileCount = 3 }, null);
        Assert.AreEqual(ReclaimChange.UncommittedChanges, more?.Change);
        Assert.AreEqual("it has 3 uncommitted files now, not 2", more?.Reason);

        Assert.IsNull(ReclaimRecheck.Compare(before, dirty with { ChangedFileCount = 1 }, null), "fewer changes lose less");
        Assert.IsNull(
            ReclaimRecheck.Compare(new GitEvidence(CleanMergedPushed, StashCount: 2), CleanMergedPushed, stashesNow: 1),
            "a popped stash loses less");
    }

    /// <summary>
    /// A row graded on <see cref="WorktreeState.Unknown"/> was graded Careful for it, and the user typed DELETE knowing nothing
    /// could be checked. Nothing it can have become is riskier than that.
    /// </summary>
    [TestMethod]
    public void ARowGradedOnNothingHasNothingToBecome()
    {
        Assert.IsTrue(WorktreeState.Unknown.IsUnknown);
        Assert.IsFalse(CleanMergedPushed.IsUnknown);
        Assert.IsNull(ReclaimRecheck.Compare(
            new GitEvidence(WorktreeState.Unknown), CleanMergedPushed with { HasUncommittedChanges = true, ChangedFileCount = 5 }, null));
    }

    [TestMethod]
    public void ConsentingToAPermanentDeleteKeepsWhatGitSaid()
    {
        ReclaimCandidate row = Graded(@"C:\src\wt", CleanMergedPushed);

        Assert.AreSame(row.GitEvidence, row.WithoutRecycleBin().GitEvidence);
    }
}
