namespace DevDriveReclaim.Tests;

/// <summary>
/// A permanent delete takes a folder whole, read-only files and all, and one that stops part-way says so with the bytes it
/// freed (Dev Config #344). <see cref="Directory.Delete(string, bool)"/> stopped at git's read-only object files after it had
/// deleted a repository's working files, and the run reported it kept, with nothing freed.
/// </summary>
/// <remarks>Everything here is deleted permanently inside a <see cref="ReclaimFixture"/>; nothing reaches the Recycle Bin.</remarks>
[TestClass]
public sealed class PermanentDeleteTests
{
    private const int Kb = 1024;

    private static ReclaimCandidate Permanent(string path, long size = 1024 * Kb, string category = "dormant-projects") =>
        new(category, path, Path.GetFileName(path), size, ReclaimRisk.Careful, "idle", "clone it again", supportsRecycleBin: false);

    private static Task<ReclaimOutcome> Run(params ReclaimCandidate[] selection) =>
        new ReclaimExecutor(_ => null).ExecuteAsync(selection, null, CancellationToken.None);

    private static void ReadOnly(string path) =>
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

    [TestMethod]
    public void WhatsLeftKeepsEverythingButItsSize()
    {
        var evidence = new Providers.GitEvidence(new Providers.WorktreeState("main", HasUncommittedChanges: true, ChangedFileCount: 1, HasUnpushedCommits: false, UnpushedCommitCount: 0, IsMerged: false));
        ReclaimCandidate scanned = new ReclaimCandidate(
            "dormant-projects", @"C:\src\old", "old", 10 * Kb, ReclaimRisk.Careful, "idle", "clone it again",
            lastUsedUtc: DateTimeOffset.UtcNow.AddDays(-400), itemCount: 30, detail: "idle 1 year", supportsRecycleBin: false)
        {
            GitEvidence = evidence,
            KeptCopy = @"C:\src\kept",
        };

        ReclaimCandidate left = scanned.WithSize(2 * Kb, 1);

        Assert.AreEqual(2 * Kb, left.SizeBytes);
        Assert.AreEqual(1, left.ItemCount);
        Assert.AreEqual(scanned, left.WithSize(scanned.SizeBytes, scanned.ItemCount), "nothing else changes");
        Assert.AreSame(evidence, left.GitEvidence);
        Assert.AreEqual(@"C:\src\kept", left.KeptCopy);
        Assert.IsFalse(left.SupportsRecycleBin);
    }

    [TestMethod]
    public async Task ReadOnlyFilesAndFoldersGoWithTheRest()
    {
        using var fixture = new ReclaimFixture();
        string project = fixture.Dir("oldproj");
        fixture.File(@"oldproj\notes.md", 4 * Kb);
        ReadOnly(fixture.File(@"oldproj\.git\objects\ab\cdef", 4 * Kb));
        ReadOnly(fixture.File(@"oldproj\.git\objects\pack\pack-1.pack", 64 * Kb));
        ReadOnly(Path.Combine(project, ".git", "objects", "pack"));

        ReclaimOutcome outcome = await Run(Permanent(project, size: 72 * Kb));

        Assert.AreEqual(ReclaimItemStatus.Deleted, outcome.Items.Single().Status, outcome.Items.Single().Message);
        Assert.IsFalse(Directory.Exists(project));
        Assert.AreEqual(72 * Kb, outcome.BytesFreed, "a whole delete frees what the scan measured, as before");
    }

    [TestMethod]
    public async Task ARepositoryGoesWholeReadOnlyGitObjectsAndAll()
    {
        if (!TestGit.IsAvailable)
        {
            Assert.Inconclusive("git is not on PATH");
        }

        using var fixture = new ReclaimFixture();
        string repo = TestGit.Repo(fixture, "oldproj");
        fixture.File(@"oldproj\src\Program.cs", 8 * Kb);
        TestGit.Run(repo, "add .");
        TestGit.Run(repo, "commit -m source");
        fixture.File(@"oldproj\zz-untracked-data.csv", 4 * Kb);
        Assert.IsTrue(
            Directory.EnumerateFiles(Path.Combine(repo, ".git", "objects"), "*", SearchOption.AllDirectories)
                .Any(f => File.GetAttributes(f).HasFlag(FileAttributes.ReadOnly)),
            "the setup is a repository whose objects git wrote read-only");

        ReclaimOutcome outcome = await Run(Permanent(repo));

        Assert.AreEqual(ReclaimItemStatus.Deleted, outcome.Items.Single().Status, outcome.Items.Single().Message);
        Assert.IsFalse(Directory.Exists(repo), "the repository went whole, .git included");
    }

    [TestMethod]
    public async Task ADeleteThatStopsPartWayIsPartlyRemovedWithTheBytesItFreed()
    {
        using var fixture = new ReclaimFixture();
        string project = fixture.Dir("oldproj");
        string[] gone =
        [
            fixture.File(@"oldproj\a.bin", 256 * Kb),
            fixture.File(@"oldproj\src\b.bin", 256 * Kb),
            fixture.File(@"oldproj\.git\objects\c.bin", 256 * Kb),
        ];
        ReadOnly(gone[2]);
        string held = fixture.File(@"oldproj\src\held-open.bin", 256 * Kb);

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ReclaimOutcome outcome = await Run(Permanent(project, size: 1024 * Kb));

            ReclaimItemOutcome item = outcome.Items.Single();
            Assert.AreEqual(ReclaimItemStatus.PartlyRemoved, item.Status);
            Assert.IsGreaterThanOrEqualTo(3 * 256 * Kb, item.Bytes, "the files that went were freed");
            Assert.IsLessThan(1024 * Kb, item.Bytes, "the file that stayed wasn't");
            Assert.AreEqual(item.Bytes, outcome.BytesFreed);
            StringAssert.StartsWith(item.Message, "partly removed — ");
            StringAssert.Contains(item.Message, "1 file or folder in it couldn't be deleted");
            Assert.IsFalse(item.Removed, "it's still there, so its row stays");
            Assert.IsTrue(item.NeedsAttention);
            Assert.AreEqual(1, outcome.FailedCount);
            Assert.AreEqual(0, outcome.RemovedCount);
        }

        Assert.IsTrue(File.Exists(held));
        Assert.IsFalse(gone.Any(File.Exists), "the rest went");
    }

    [TestMethod]
    public async Task NothingDeletedIsStillAFailureWithNothingFreed()
    {
        using var fixture = new ReclaimFixture();
        string project = fixture.Dir("locked");
        string held = fixture.File(@"locked\held-open.bin", 64 * Kb);

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ReclaimOutcome outcome = await Run(Permanent(project));

            Assert.AreEqual(ReclaimItemStatus.Failed, outcome.Items.Single().Status);
            Assert.AreEqual(0, outcome.BytesFreed);
        }
    }

    [TestMethod]
    public async Task AJunctionInsideIsRemovedAsALinkAndNotFollowed()
    {
        using var fixture = new ReclaimFixture();
        string outside = fixture.File(@"outside\keep.bin", 64 * Kb);
        string project = fixture.Dir("oldproj");
        fixture.File(@"oldproj\a.bin", 64 * Kb);
        string link = fixture.Junction(@"oldproj\linked", Path.GetDirectoryName(outside)!);
        if (!File.Exists(Path.Combine(link, "keep.bin")))
        {
            Assert.Inconclusive("the junction wasn't made");
        }

        ReclaimOutcome outcome = await Run(Permanent(project));

        Assert.AreEqual(ReclaimItemStatus.Deleted, outcome.Items.Single().Status, outcome.Items.Single().Message);
        Assert.IsFalse(Directory.Exists(project));
        Assert.IsTrue(File.Exists(outside), "the delete followed a junction out of the folder");
    }

    [TestMethod]
    public async Task ARowInsideAPartlyRemovedFolderIsAskedOfTheDisk()
    {
        using var fixture = new ReclaimFixture();
        string project = fixture.Dir("oldproj");
        string bin = Path.GetDirectoryName(fixture.File(@"oldproj\bin\out.dll", 64 * Kb))!;
        string src = Path.GetDirectoryName(fixture.File(@"oldproj\src\held-open.bin", 64 * Kb))!;

        using (new FileStream(Path.Combine(src, "held-open.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ReclaimOutcome outcome = await Run(Permanent(project), Permanent(bin, category: "build-outputs"), Permanent(src));

            Assert.AreEqual(ReclaimItemStatus.PartlyRemoved, outcome.Items.Single(i => i.Candidate.Path == project).Status);
            Assert.AreEqual(ReclaimItemStatus.Absorbed, outcome.Items.Single(i => i.Candidate.Path == bin).Status, "it went");
            ReclaimItemOutcome stayed = outcome.Items.Single(i => i.Candidate.Path == src);
            Assert.AreEqual(ReclaimItemStatus.Failed, stayed.Status);
            StringAssert.Contains(stayed.Message, "only partly removed");
        }
    }
}
