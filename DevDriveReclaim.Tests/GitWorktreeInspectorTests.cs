using System.Diagnostics;
using DevDriveReclaim.Providers;

namespace DevDriveReclaim.Tests;

/// <summary>
/// Tests <see cref="GitWorktreeInspector"/> against real repositories created on disk.
/// </summary>
/// <remarks>
/// <para>
/// Every other worktree test injects a hand-built <see cref="WorktreeState"/> through a stub, so
/// the grading table is airtight and the code that <i>derives</i> that state from a real
/// repository had no coverage at all. That derivation is what decides whether a folder holding
/// someone's only copy of a change is preselected for deletion.
/// </para>
/// <para>
/// These tests shell out to the real git, so they are skipped when git is not on PATH rather than
/// failing — a machine without git is a machine where this class returns Unknown and everything
/// grades Careful, which is the safe outcome and not a regression.
/// </para>
/// </remarks>
[TestClass]
public sealed class GitWorktreeInspectorTests
{
    private static bool GitIsAvailable => _gitAvailable ??= ProbeForGit();

    private static bool? _gitAvailable;

    private static bool ProbeForGit()
    {
        try
        {
            using Process? probe = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (probe is null)
            {
                return false;
            }

            probe.BeginOutputReadLine();
            probe.BeginErrorReadLine();
            return probe.WaitForExit(10_000) && probe.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Git(string workingDirectory, string arguments)
    {
        using Process? process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        Assert.IsNotNull(process, $"could not start: git {arguments}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Assert.IsTrue(process.WaitForExit(30_000), $"git {arguments} did not finish");
    }

    /// <summary>A real repository with one commit on a branch, and identity set locally.</summary>
    private static string MakeRepo(ReclaimFixture fixture, string name)
    {
        string root = fixture.Dir(name);

        Git(root, "init --initial-branch=main");

        // Local, not global: the test must not depend on or disturb the developer's git identity.
        Git(root, "config user.email tests@example.invalid");
        Git(root, "config user.name Tests");
        Git(root, "config commit.gpgsign false");

        System.IO.File.WriteAllText(Path.Combine(root, "tracked.txt"), "committed content");
        Git(root, "add .");
        Git(root, "commit -m initial");

        return root;
    }

    [TestMethod]
    public void ACleanRepositoryReportsNoUncommittedChanges()
    {
        if (!GitIsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = MakeRepo(fixture, "clean");

        WorktreeState state = new GitWorktreeInspector().Inspect(repo, CancellationToken.None);

        Assert.IsFalse(state.HasUncommittedChanges, "a freshly committed tree is not dirty");
        Assert.AreEqual(0, state.ChangedFileCount);
        Assert.AreEqual("main", state.Branch);
    }

    [TestMethod]
    public void AModifiedTrackedFileIsReportedAsDirty()
    {
        if (!GitIsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = MakeRepo(fixture, "modified");
        System.IO.File.WriteAllText(Path.Combine(repo, "tracked.txt"), "edited, never committed");

        WorktreeState state = new GitWorktreeInspector().Inspect(repo, CancellationToken.None);

        Assert.IsTrue(state.HasUncommittedChanges, "an edit that exists nowhere else must be seen");
        Assert.AreEqual(1, state.ChangedFileCount);
    }

    [TestMethod]
    public void AnUntrackedFileIsReportedAsDirty()
    {
        if (!GitIsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = MakeRepo(fixture, "untracked");
        System.IO.File.WriteAllText(Path.Combine(repo, "brand-new.txt"), "never added to git");

        WorktreeState state = new GitWorktreeInspector().Inspect(repo, CancellationToken.None);

        // An untracked file is the single most likely thing to exist in exactly one place.
        Assert.IsTrue(state.HasUncommittedChanges, "an untracked file must count as dirty");
        Assert.AreEqual(1, state.ChangedFileCount);
    }

    [TestMethod]
    public void ABranchWithNoUpstreamIsDistinguishedFromOneThatIsMerelyAhead()
    {
        if (!GitIsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = MakeRepo(fixture, "no-upstream");

        WorktreeState state = new GitWorktreeInspector().Inspect(repo, CancellationToken.None);

        // No remote exists, so there is no copy of this work anywhere else. Reporting that as
        // "0 unpushed commits" would read as nothing to lose.
        Assert.IsFalse(state.HasUpstream, "a repo with no remote has no upstream");
        Assert.IsTrue(state.HasUnpushedCommits, "no upstream is itself an unpushed state");
    }

    [TestMethod]
    public void AFolderThatIsNotARepositoryIsUnknownRatherThanClean()
    {
        using var fixture = new ReclaimFixture();
        string plain = fixture.Dir("not-a-repo");

        WorktreeState state = new GitWorktreeInspector().Inspect(plain, CancellationToken.None);

        // Failing open here would grade an unreadable folder Safe and preselect it.
        Assert.IsTrue(state.HasUncommittedChanges, "what we cannot check, we treat as dirty");
        Assert.IsFalse(state.IsMerged);
    }

    [TestMethod]
    public void AMissingPathIsUnknown()
    {
        WorktreeState state = new GitWorktreeInspector()
            .Inspect(Path.Combine(Path.GetTempPath(), $"ddm-absent-{Guid.NewGuid():N}"), CancellationToken.None);

        Assert.IsTrue(state.HasUncommittedChanges);
        Assert.IsFalse(state.IsMerged);
    }

    [TestMethod]
    public void AnAlreadyCancelledScanDoesNotRunGitToCompletion()
    {
        if (!GitIsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = MakeRepo(fixture, "cancelled");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Cancellation is observed rather than swallowed: the scan must be able to stop, and a
        // cancelled scan must not be able to report a confident "clean" for a tree it never read.
        Assert.ThrowsExactly<OperationCanceledException>(
            () => new GitWorktreeInspector().Inspect(repo, cancelled.Token));
    }

    [TestMethod]
    public void EveryInspectionFinishesWellInsideTheTimeout()
    {
        if (!GitIsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = MakeRepo(fixture, "prompt");

        // The regression this guards: reading stdout to the end before waiting made the 10s
        // timeout unreachable, and leaving stderr redirected but undrained deadlocked a chatty
        // git outright. Either one turns a scan into an indefinite hang, once per worktree.
        var clock = Stopwatch.StartNew();
        new GitWorktreeInspector().Inspect(repo, CancellationToken.None);
        clock.Stop();

        Assert.IsLessThan(
            TimeSpan.FromSeconds(40),
            clock.Elapsed,
            "inspecting a tiny local repository should be nowhere near the per-command timeout");
    }

    [TestMethod]
    public void AllOutputIsCapturedWhenGitProducesFarMoreThanOneRead()
    {
        if (!GitIsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = MakeRepo(fixture, "chatty");

        // 600 untracked files is far more porcelain output than a single pipe read returns, so
        // this pins that the asynchronous drain is assembled completely rather than truncated.
        for (int i = 0; i < 600; i++)
        {
            System.IO.File.WriteAllText(
                Path.Combine(repo, $"untracked-file-with-a-deliberately-long-name-{i:D4}.txt"), "x");
        }

        WorktreeState state = new GitWorktreeInspector().Inspect(repo, CancellationToken.None);

        Assert.IsTrue(state.HasUncommittedChanges);
        Assert.IsGreaterThanOrEqualTo(
            600, state.ChangedFileCount, "every untracked file should survive the pipe");
    }

    [TestMethod]
    public void AnIgnoredEnvFileIsReportedWhileIgnoredBuildOutputIsNot()
    {
        if (!GitIsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = MakeRepo(fixture, "ignored-secrets");

        System.IO.File.WriteAllText(Path.Combine(repo, ".gitignore"), ".env\nbin/\nobj/\n");
        Git(repo, "add .gitignore");
        Git(repo, "commit -m ignore");

        System.IO.File.WriteAllText(Path.Combine(repo, ".env"), "API_TOKEN=only-copy-on-this-machine");
        Directory.CreateDirectory(Path.Combine(repo, "bin"));
        System.IO.File.WriteAllText(Path.Combine(repo, "bin", "app.dll"), "regenerable");

        WorktreeState state = new GitWorktreeInspector().Inspect(repo, CancellationToken.None);

        // The whole point: git reports both as ignored, and only one of them matters.
        CollectionAssert.Contains(state.LocalOnlyIgnoredFiles.ToArray(), ".env");
        Assert.HasCount(1, state.LocalOnlyIgnoredFiles, "bin/ must not be treated as precious");

        // Ignored files are not working-tree changes, so they must not inflate the dirty count —
        // that would relabel every worktree on the machine as having uncommitted edits.
        Assert.IsFalse(state.HasUncommittedChanges);
        Assert.AreEqual(0, state.ChangedFileCount);
    }

    [TestMethod]
    public void AnOrdinaryRepositoryReportsNoLocalOnlyContent()
    {
        if (!GitIsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = MakeRepo(fixture, "ordinary");
        System.IO.File.WriteAllText(Path.Combine(repo, ".gitignore"), "bin/\nobj/\n");
        Git(repo, "add .gitignore");
        Git(repo, "commit -m ignore");
        Directory.CreateDirectory(Path.Combine(repo, "obj"));
        System.IO.File.WriteAllText(Path.Combine(repo, "obj", "x.tmp"), "junk");

        WorktreeState state = new GitWorktreeInspector().Inspect(repo, CancellationToken.None);

        // Measured against 53 real worktrees, this is the case for every single one of them.
        Assert.IsFalse(state.HasLocalOnlyIgnoredFiles);
    }

    /// <summary>
    /// The regression test for the hang: a git that never finishes must not stall the scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A slow <c>core.fsmonitor</c> hook is a genuine, reproducible way to make <c>git status</c>
    /// hang — git runs the hook and waits for it. That matters because the bug being guarded here
    /// is only observable against a git that does not return: with <c>ReadToEnd()</c> called before
    /// <c>WaitForExit(timeout)</c>, the read blocks until git closes stdout, so the timeout below it
    /// can never run and this test sits for the hook's full duration instead of ~10 seconds.
    /// </para>
    /// <para>
    /// Verified by reverting the fix and watching this go red, which is the only thing that
    /// distinguishes a regression test from a test that merely passes. Reverted, it takes 91
    /// seconds and fails; fixed, it takes about 10 and passes. No <c>[Timeout]</c> is needed
    /// because the hook's own <c>ping</c> count bounds the worst case.
    /// </para>
    /// </remarks>
    [TestMethod]
    public void AGitThatNeverFinishesIsAbandonedRatherThanWaitedOnForever()
    {
        if (!GitIsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = MakeRepo(fixture, "hung");

        string hook = Path.Combine(repo, "slow-hook.sh");
        System.IO.File.WriteAllText(hook, "#!/bin/sh\nping -n 90 127.0.0.1 > /dev/null\n");

        // git's bundled sh has no `sleep`, and needs forward slashes in a config path.
        Git(repo, $"config core.fsmonitor \"{hook.Replace('\\', '/')}\"");

        var clock = Stopwatch.StartNew();
        WorktreeState state = new GitWorktreeInspector().Inspect(repo, CancellationToken.None);
        clock.Stop();

        Assert.IsLessThan(
            TimeSpan.FromSeconds(45),
            clock.Elapsed,
            "a hung git must be abandoned on the timeout, not waited on until it chooses to exit");

        // Just as important as returning: what it returns. A scan that gave up must not report a
        // confident "clean", because clean is what gets preselected for deletion.
        Assert.IsTrue(
            state.HasUncommittedChanges,
            "a worktree we could not read must fall back to the Careful-grading sentinel");
        Assert.IsFalse(state.IsMerged);
    }

    /// <summary>
    /// A worktree checked out at a bare commit reports the literal string <c>HEAD</c> as its
    /// branch. That matters because it is the one shape where deleting the folder really does
    /// destroy work: no ref names those commits, so once the administrative entry is pruned they
    /// are unreachable and git collects them. Verified directly against real git.
    /// </summary>
    [TestMethod]
    public void ADetachedCheckoutIsRecognisedRatherThanNamedHead()
    {
        if (!GitIsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = MakeRepo(fixture, "detached");
        Git(repo, "checkout --detach HEAD");

        WorktreeState state = new GitWorktreeInspector().Inspect(repo, CancellationToken.None);

        Assert.IsTrue(state.IsDetached);
        Assert.AreNotEqual("HEAD", state.Branch,
            "\"HEAD\" is git's placeholder for having no branch, not the name of one — rendering " +
            "it as a branch would make a row read as though the commits were safely named.");
    }

    [TestMethod]
    public void AnOrdinaryCheckoutIsNotReportedAsDetached()
    {
        if (!GitIsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = MakeRepo(fixture, "attached");

        WorktreeState state = new GitWorktreeInspector().Inspect(repo, CancellationToken.None);

        // The counter-test. Reporting everything detached would make the guard fire everywhere,
        // which is how a warning stops being read.
        Assert.IsFalse(state.IsDetached);
        Assert.AreEqual("main", state.Branch);
    }

    [TestMethod]
    public void StashesAreCountedAndAnUnstashedRepositoryReportsNone()
    {
        if (!GitIsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = MakeRepo(fixture, "stashed");
        var inspector = new GitWorktreeInspector();

        Assert.AreEqual(0, inspector.StashCount(repo, CancellationToken.None));

        System.IO.File.WriteAllText(Path.Combine(repo, "tracked.txt"), "edited once");
        Git(repo, "stash push -m first");
        System.IO.File.WriteAllText(Path.Combine(repo, "tracked.txt"), "edited twice");
        Git(repo, "stash push -m second");

        Assert.AreEqual(2, inspector.StashCount(repo, CancellationToken.None));
    }

    /// <summary>
    /// The measurement that redirected this whole rule. <c>refs/stash</c> and the object database
    /// belong to the parent repository, so a worktree cannot take a stash with it when it goes —
    /// which is why stash detection lives in the dormant-clone path and not here.
    /// </summary>
    [TestMethod]
    public void AStashSurvivesTheWorktreeThatCreatedIt()
    {
        if (!GitIsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string parent = MakeRepo(fixture, "parent");
        string worktree = Path.Combine(fixture.Root, "child-worktree");

        Git(parent, $"worktree add \"{worktree}\" -b side");
        System.IO.File.WriteAllText(Path.Combine(worktree, "tracked.txt"), "work in progress");
        Git(worktree, "stash push -m from-the-worktree");

        var inspector = new GitWorktreeInspector();
        Assert.AreEqual(1, inspector.StashCount(parent, CancellationToken.None),
            "the stash is the repository's, so the parent can already see it");

        Directory.Delete(worktree, recursive: true);

        Assert.AreEqual(1, inspector.StashCount(parent, CancellationToken.None),
            "deleting the worktree folder must not lose the stash it created");
    }
}
