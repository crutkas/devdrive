using System.Collections.Concurrent;
using DevDriveReclaim.Providers;
using DevDriveStorage;
using DevDriveStorage.Live;

namespace DevDriveReclaim.Tests;

/// <summary>
/// One walk feeds both Space and Reclaim: given the Space scan's <see cref="IVolumeIndex"/>, the detectors find the same
/// candidates they find by reading the disk, and read none of the folders the walk already read.
/// </summary>
[TestClass]
public sealed class OneWalkTests
{
    private const long Mb = 1024 * 1024;

    [TestMethod]
    public async Task TheDetectorsFindTheSameThingsFromTheWalkWithoutReadingAgain()
    {
        using var fixture = new ReclaimFixture();
        string app = fixture.Repo("app");
        fixture.File(@"app\bin\Debug\app.dll", 3 * Mb);
        fixture.File(@"app\obj\project.assets.json", 2 * Mb);
        fixture.File(@"app\node_modules\left-pad\index.js", 2 * Mb);
        fixture.File(@"app\src\Program.cs", 1000);
        string old = fixture.Repo("old");
        fixture.File(@"old\data.bin", 3 * Mb);
        fixture.Age(old, TimeSpan.FromDays(400));
        fixture.Worktree("feature", Path.Combine(app, ".git", "worktrees", "feature"));
        fixture.File(@"feature\bin\out.dll", 2 * Mb);
        byte[] same = new byte[5 * (int)Mb];
        new Random(7).NextBytes(same);
        fixture.FileWithContent(@"copies\one\big.bin", same);
        fixture.FileWithContent(@"copies\two\big.bin", same);

        var git = new StubWorktreeInspector(new WorktreeState("main", HasUncommittedChanges: false, ChangedFileCount: 0, HasUnpushedCommits: false, UnpushedCommitCount: 0, IsMerged: true));
        IReclaimProvider[] Providers() =>
        [
            new BuildOutputReclaimProvider(),
            new DormantProjectReclaimProvider(git),
            new WorktreeReclaimProvider(git),
            new DuplicateFileReclaimProvider(),
        ];
        ReclaimScanContext Context(IVolumeIndex? index) => new([fixture.Root], [fixture.Root], minimumCandidateBytes: Mb) { Index = index };

        (ReclaimResult fromDisk, List<string> diskReads) = await Traced(fixture.Root, () => new ReclaimEngine(Providers()).ScanAsync(Context(null), null, CancellationToken.None));

        IVolumeIndex index = (await new LiveStorageSnapshotSource { KeepIndex = true }
            .GetSnapshotAsync(new StorageSnapshotRequest(fixture.Root), null, CancellationToken.None)).TakeIndex()!;
        (ReclaimResult fromWalk, List<string> walkReads) = await Traced(fixture.Root, () => new ReclaimEngine(Providers()).ScanAsync(Context(index), null, CancellationToken.None));

        static string[] Rows(ReclaimResult result) =>
            [.. result.AllCandidates.Select(c => $"{c.CategoryId}|{c.Path.ToUpperInvariant()}|{c.SizeBytes}|{c.Risk}|{c.ItemCount}").Order()];
        Assert.IsNotEmpty(fromDisk.AllCandidates);
        CollectionAssert.AreEqual(Rows(fromDisk), Rows(fromWalk));
        Assert.IsNotEmpty(diskReads, "without the walk, the detectors read the folders themselves");
        Assert.IsEmpty(walkReads, "with the walk, they read none of them again: " + string.Join(", ", walkReads));
    }

    /// <summary>Runs <paramref name="scan"/> and lists the folders under <paramref name="root"/> it read.</summary>
    private static async Task<(T Result, List<string> Reads)> Traced<T>(string root, Func<Task<T>> scan)
    {
        var reads = new ConcurrentQueue<string>();
        Action<string> listener = path =>
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                reads.Enqueue(path);
            }
        };
        DirectoryReadTrace.Listener += listener;
        try
        {
            return (await scan(), [.. reads]);
        }
        finally
        {
            DirectoryReadTrace.Listener -= listener;
        }
    }
}
