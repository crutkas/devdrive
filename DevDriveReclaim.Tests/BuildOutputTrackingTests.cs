using DevDriveReclaim.Providers;

namespace DevDriveReclaim.Tests;

/// <summary>
/// A folder named like build output isn't generated output if git tracks files in it (Dev Config #351). A repository's
/// committed <c>build</c> folder of pipeline definitions and signing scripts was offered as Safe, "nothing here was written by
/// hand", on its name alone.
/// </summary>
[TestClass]
public sealed class BuildOutputTrackingTests
{
    private const long Kb = 1024;

    private static ReclaimScanContext Context(ReclaimFixture fixture) =>
        new([Path.GetPathRoot(fixture.Root)!], [fixture.Root], minimumCandidateBytes: 1);

    private static async Task<IReadOnlyList<ReclaimCandidate>> Scan(ReclaimFixture fixture, IWorktreeInspector? git = null) =>
        await new BuildOutputReclaimProvider(git).ScanAsync(Context(fixture), null, CancellationToken.None);

    /// <summary>A fake repository git would recognise (a <c>.git</c> with a <c>HEAD</c>), with three output folders.</summary>
    private static string RepoWithOutputs(ReclaimFixture fixture)
    {
        string repo = fixture.Repo("app");
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        fixture.File(@"app\build\pipeline.yml", 64 * Kb);
        fixture.File(@"app\bin\app.dll", 64 * Kb);
        fixture.File(@"app\src\obj\app.obj", 64 * Kb);
        return repo;
    }

    [TestMethod]
    public async Task TrackedBuildAndBinFoldersAreNotOfferedButAnIgnoredBinIs()
    {
        if (!TestGit.IsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = TestGit.Repo(fixture, "app");

        // Committed by hand: a build folder of pipeline files, and a tool force-added past the ignore rule.
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "bin/\n");
        fixture.File(@"app\build\pipeline.yml", 64 * Kb);
        fixture.File(@"app\build\sign.ps1", 64 * Kb);
        fixture.File(@"app\tools\bin\helper.exe", 64 * Kb);
        TestGit.Run(repo, "add .gitignore build");
        TestGit.Run(repo, "add -f tools/bin/helper.exe");
        TestGit.Run(repo, "commit -m tools");

        // An edit nobody has committed yet, and output beside the committed files: the folder is still not generated output.
        File.AppendAllText(Path.Combine(repo, "build", "sign.ps1"), "# uncommitted edit\n");
        fixture.File(@"app\build\artifact.zip", 64 * Kb);

        // Real output: ignored, nothing in it committed.
        string output = Path.GetDirectoryName(fixture.File(@"app\src\app\bin\app.dll", 64 * Kb))!;

        IReadOnlyList<ReclaimCandidate> found = await Scan(fixture);

        ReclaimCandidate only = found.Single();
        Assert.AreEqual(output, only.Path, ignoreCase: true);
        Assert.AreEqual(ReclaimRisk.Safe, only.Risk);
    }

    [TestMethod]
    public async Task OneQuestionToGitPerRepositoryAndWhatItTracksIsLeftOut()
    {
        using var fixture = new ReclaimFixture();
        RepoWithOutputs(fixture);
        var asked = new List<string>();
        var git = new GitWorktreeInspector((_, arguments, _) =>
        {
            asked.Add(arguments);
            return new GitResult(0, "BUILD/pipeline.yml\0");
        });

        IReadOnlyList<ReclaimCandidate> found = await Scan(fixture, git);

        Assert.HasCount(1, asked, "one git call for the repository's folders, not one per folder");
        StringAssert.Contains(asked[0], "ls-files -z -- ");
        StringAssert.Contains(asked[0], "\":(literal,icase)src/obj\"");
        CollectionAssert.AreEquivalent(
            new[] { "app · bin", @"app · src\obj" }, found.Select(c => c.DisplayName).ToArray(), "build is tracked (git's case differs)");
        Assert.IsTrue(found.All(c => c.Risk == ReclaimRisk.Safe));
    }

    [TestMethod]
    public async Task WhenGitCantAnswerNothingIsCalledSafe()
    {
        using var fixture = new ReclaimFixture();
        RepoWithOutputs(fixture);

        IReadOnlyList<ReclaimCandidate> found = await Scan(fixture, new GitWorktreeInspector((_, _, _) => null));

        Assert.HasCount(3, found);
        Assert.IsTrue(found.All(c => c.Risk == ReclaimRisk.Check), "a folder git couldn't vouch for isn't preselected");
        Assert.IsTrue(found.All(c => c.Reason.Contains("git couldn't say", StringComparison.Ordinal)));
        Assert.IsTrue(found.All(c => c.RecoveryHint.Contains("git checkout --", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AGitThatFailsIsNotAnAnswerEither()
    {
        using var fixture = new ReclaimFixture();
        RepoWithOutputs(fixture);

        // "detected dubious ownership", a corrupt index: git exits non-zero and lists nothing.
        IReadOnlyList<ReclaimCandidate> found = await Scan(
            fixture, new GitWorktreeInspector((_, _, _) => new GitResult(128, string.Empty)));

        Assert.IsTrue(found.All(c => c.Risk == ReclaimRisk.Check));
    }

    [TestMethod]
    public async Task AFolderThatIsntARepositoryToGitKeepsTheNamesAnswer()
    {
        using var fixture = new ReclaimFixture();

        // A .git folder with no HEAD isn't a repository to git either, so there's nothing it could track.
        fixture.Repo("app");
        fixture.File(@"app\bin\app.dll", 64 * Kb);
        int calls = 0;

        IReadOnlyList<ReclaimCandidate> found = await Scan(
            fixture, new GitWorktreeInspector((_, _, _) => { calls++; return null; }));

        Assert.AreEqual(0, calls);
        Assert.AreEqual(ReclaimRisk.Safe, found.Single().Risk);
    }

    [TestMethod]
    public void AWorktreesGitFileIsARepositoryOnlyWhenItsGitdirIs()
    {
        using var fixture = new ReclaimFixture();
        string gitDir = fixture.Dir("main", ".git", "worktrees", "feature");
        string live = fixture.Worktree("feature", gitDir);
        string dangling = fixture.Worktree("dangling", Path.Combine(fixture.Root, "gone", ".git", "worktrees", "x"));

        Assert.IsFalse(GitWorktreeInspector.IsRepository(live), "no HEAD in its gitdir yet");
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/feature\n");
        Assert.IsTrue(GitWorktreeInspector.IsRepository(live));
        Assert.IsFalse(GitWorktreeInspector.IsRepository(dangling));
        Assert.IsFalse(GitWorktreeInspector.IsRepository(fixture.Dir("plain")));
    }
}
