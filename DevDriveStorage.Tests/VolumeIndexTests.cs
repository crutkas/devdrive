using System.Collections.Concurrent;
using DevDriveStorage.Live;

namespace DevDriveStorage.Tests;

/// <summary>
/// Covers <see cref="IVolumeIndex"/>, what the Space walk keeps for the Reclaim detectors: every answer it gives is the one the
/// single-purpose walker would give, a folder it didn't read completely is left to the disk, and the walk reads each folder once.
/// </summary>
[TestClass]
public sealed class VolumeIndexTests
{
    private const long Mb = 1024 * 1024;

    private LiveScanFixture _fixture = null!;

    [TestInitialize]
    public void Init() => _fixture = new LiveScanFixture();

    [TestCleanup]
    public void Cleanup() => _fixture.Dispose();

    [TestMethod]
    public void EveryFolderMeasuresAsTheMeasurerMeasuresIt()
    {
        _fixture.File("a.bin", 8192);
        _fixture.File(@"src\app\bin\out.dll", 70_001);
        _fixture.File(@"src\app\obj\x\y.obj", 4096);
        _fixture.File(@"src\app\Program.cs", 300);
        _fixture.File(@"elsewhere\big.bin", 5 * Mb);
        _fixture.Dir("empty");
        _fixture.Junction(@"src\link", Path.Combine(_fixture.Root, "elsewhere"));

        IVolumeIndex index = Scan();

        foreach (string folder in Directory.EnumerateDirectories(_fixture.Root, "*", SearchOption.AllDirectories).Prepend(_fixture.Root))
        {
            if (folder.Contains(@"\link\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A junction isn't walked: the index leaves it to the measurer, which doesn't list it either.
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            {
                Assert.IsFalse(index.TryMeasure(folder, out _), folder);
                continue;
            }

            Assert.IsTrue(index.TryMeasure(folder, out DirectoryMeasurement fromIndex), folder);
            Assert.AreEqual(DirectoryMeasurer.Measure(folder), fromIndex, folder);
        }
    }

    [TestMethod]
    public void AListingHasTheSubfoldersJunctionsAndTheGitEntry()
    {
        _fixture.Dir(@"repo\.git");
        _fixture.File(@"repo\src\a.cs", 10);
        _fixture.File(@"worktree\.git", 40);
        _fixture.Dir(@"plain\sub");
        _fixture.Junction(@"plain\link", Path.Combine(_fixture.Root, "repo"));

        IVolumeIndex index = Scan();

        Assert.IsTrue(index.TryList(_fixture.Root, out VolumeIndexListing root));
        CollectionAssert.AreEquivalent(new[] { "repo", "worktree", "plain" }, root.Folders.Select(f => f.Name).ToArray());
        Assert.AreEqual(GitEntry.None, root.Git);
        Assert.IsTrue(index.TryList(Path.Combine(_fixture.Root, "repo"), out VolumeIndexListing repo));
        Assert.AreEqual(GitEntry.Folder, repo.Git);
        Assert.IsTrue(index.TryList(Path.Combine(_fixture.Root, "worktree"), out VolumeIndexListing worktree));
        Assert.AreEqual(GitEntry.File, worktree.Git);
        Assert.IsTrue(index.TryList(Path.Combine(_fixture.Root, "plain"), out VolumeIndexListing plain));
        CollectionAssert.AreEquivalent(
            Directory.EnumerateDirectories(Path.Combine(_fixture.Root, "plain")).Select(Path.GetFileName).ToArray(),
            plain.Folders.Select(f => f.Name).ToArray());
        Assert.AreNotEqual((FileAttributes)0, plain.Folders.Single(f => f.Name == "link").Attributes & FileAttributes.ReparsePoint);
    }

    [TestMethod]
    public void LargeFilesAreTheOnesTheFileWalkerFinds()
    {
        _fixture.File(@"one\a.bin", 5 * Mb);
        _fixture.File(@"one\small.bin", 3 * Mb);
        _fixture.File(@"one\two\b.bin", 4 * Mb);
        _fixture.File(@"target\c.bin", 6 * Mb);
        _fixture.Junction(@"one\link", Path.Combine(_fixture.Root, "target"));

        IVolumeIndex index = Scan();

        string start = Path.Combine(_fixture.Root, "one");
        Assert.IsTrue(index.TryFindLargeFiles(start, out IReadOnlyList<(string FullPath, FastDirEntry Entry)> fromIndex));
        var expected = FastDirectoryWalker.EnumerateFiles(start).Where(f => f.Entry.ApparentBytes >= index.LargeFileBytes)
            .Select(f => (f.FullPath.ToUpperInvariant(), f.Entry.AllocatedBytes, f.Entry.ApparentBytes)).ToList();
        CollectionAssert.AreEquivalent(expected, fromIndex.Select(f => (f.FullPath.ToUpperInvariant(), f.Entry.AllocatedBytes, f.Entry.ApparentBytes)).ToList());
        Assert.HasCount(2, fromIndex);
    }

    [TestMethod]
    public void AFolderTheWalkCouldntReadIsLeftToTheDisk()
    {
        _fixture.File(@"open\a.bin", 4096);
        string denied = _fixture.Dir("denied");
        _fixture.File(@"denied\b.bin", 4096);
        _fixture.DenyListing(denied);

        IVolumeIndex index = Scan();

        Assert.IsTrue(index.TryMeasure(Path.Combine(_fixture.Root, "open"), out _));
        Assert.IsFalse(index.TryMeasure(denied, out _));
        Assert.IsFalse(index.TryList(denied, out _));
        Assert.IsFalse(index.TryMeasure(_fixture.Root, out _), "a folder with an unread folder beneath it isn't measured whole");
        Assert.IsFalse(index.TryMeasure(Path.Combine(Path.GetTempPath(), "not-in-this-walk"), out _));
    }

    [TestMethod]
    public void TheIndexIsTakenOnceAndOnlyWhenKept()
    {
        _fixture.File("a.bin", 10);

        StorageSnapshot kept = new LiveStorageSnapshotSource { KeepIndex = true }
            .GetSnapshotAsync(new StorageSnapshotRequest(_fixture.Root), null, CancellationToken.None).GetAwaiter().GetResult();
        Assert.IsNotNull(kept.TakeIndex());
        Assert.IsNull(kept.TakeIndex());

        StorageSnapshot plain = new LiveStorageSnapshotSource()
            .GetSnapshotAsync(new StorageSnapshotRequest(_fixture.Root), null, CancellationToken.None).GetAwaiter().GetResult();
        Assert.IsNull(plain.TakeIndex());
    }

    [TestMethod]
    public void TheWalkReadsEachFolderOnce()
    {
        _fixture.File(@"a\b\c.bin", 10);
        _fixture.File(@"a\d\e.bin", 10);
        var reads = new ConcurrentBag<string>();
        Action<string> listener = path =>
        {
            if (path.StartsWith(_fixture.Root, StringComparison.OrdinalIgnoreCase))
            {
                reads.Add(path.TrimEnd('\\').ToUpperInvariant());
            }
        };

        DirectoryReadTrace.Listener += listener;
        try
        {
            Scan();
        }
        finally
        {
            DirectoryReadTrace.Listener -= listener;
        }

        Assert.HasCount(4, reads);
        Assert.AreEqual(reads.Count, reads.Distinct().Count());
    }

    private IVolumeIndex Scan() =>
        new LiveStorageSnapshotSource { KeepIndex = true }
            .GetSnapshotAsync(new StorageSnapshotRequest(_fixture.Root), null, CancellationToken.None).GetAwaiter().GetResult()
            .TakeIndex()!;
}
