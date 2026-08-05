using DevDriveStorage;

namespace DevDriveStorage.Tests;

/// <summary>
/// A streaming scan knows a folder exists before it knows how big it is. These tests pin the
/// difference between the two: an unwalked folder reads as an em dash, because rendering it as
/// "0 bytes" claims a measurement that was never taken.
/// </summary>
[TestClass]
public sealed class UnmeasuredNodeTests
{
    private LiveScanFixture _fixture = null!;

    [TestInitialize]
    public void Init() => _fixture = new LiveScanFixture();

    [TestCleanup]
    public void Cleanup() => _fixture.Dispose();

    private static StorageNode Folder(long size = 0, int items = 0) => StorageTestBuilder.Node(
        Guid.Parse("30000000-0000-0000-0000-000000000001"),
        StorageTestBuilder.RootId,
        "pending",
        @"M:\pending",
        StorageNodeKind.Folder,
        size,
        items);

    [TestMethod]
    public void AMeasuredNodeStillFormatsItsBytes()
    {
        StorageNode node = Folder(size: 4096, items: 2);

        Assert.IsTrue(node.IsMeasured, "measured is the default; only a live scan says otherwise");
        Assert.AreEqual("4.10 KB", node.SizeDisplay);
        Assert.AreEqual("2", node.ItemCountDisplay);
    }

    [TestMethod]
    public void AnUnwalkedFolderReadsAsAnEmDashRatherThanZero()
    {
        StorageNode node = Folder() with { IsMeasured = false };

        Assert.AreEqual("—", node.SizeDisplay, "zero bytes is a measurement we have not taken");
        Assert.AreEqual("—", node.ItemCountDisplay);
        Assert.AreEqual("—", node.LogicalDisplay);
        Assert.IsFalse(node.HasLogicalDifference, "nothing can differ from a number we do not have");
    }

    [TestMethod]
    public void AnEmptyFolderWeDidWalkStillReadsAsZero()
    {
        // The pair that makes the flag worth having: identical bytes, opposite meanings.
        StorageNode walked = Folder();

        Assert.AreEqual("0 B", walked.SizeDisplay);
        Assert.AreEqual("0", walked.ItemCountDisplay);
    }

    [TestMethod]
    public void TheFlagSurvivesTheCopyAStreamingScanMakes()
    {
        StorageNode pending = Folder() with { IsMeasured = false };

        Assert.IsFalse((pending with { }).IsMeasured);
        Assert.IsFalse((pending with { SizeBytes = 10 }).IsMeasured);
    }

    [TestMethod]
    public void ARowHidesItsShareOfScopeUntilItHasBeenWalked()
    {
        var row = new StorageRowViewModel(Folder() with { IsMeasured = false }, 1_000, 0);

        Assert.AreEqual("—", row.SizeDisplay);
        Assert.AreEqual("—", row.ShareDisplay, "0% of scope is a claim, not an absence");
        StringAssert.Contains(row.AccessibleDescription, "—");
    }

    [TestMethod]
    public void ARowRepaintsWhenAnEmptyFolderIsFinallyWalked()
    {
        // The regression this flag exists to avoid. An empty folder is zero bytes before and after
        // it is walked, so a size comparison sees nothing move and the row would keep showing an em
        // dash for the rest of the scan.
        var row = new StorageRowViewModel(Folder() with { IsMeasured = false }, 1_000, 0);
        var raised = new List<string>();
        row.PropertyChanged += (_, args) => raised.Add(args.PropertyName!);

        row.Adopt(Folder(), 1_000, 0);

        Assert.AreEqual("0 B", row.SizeDisplay);
        Assert.AreEqual("0%", row.ShareDisplay);
        CollectionAssert.Contains(raised, nameof(StorageRowViewModel.SizeDisplay));
        CollectionAssert.Contains(raised, nameof(StorageRowViewModel.ShareDisplay));
    }

    [TestMethod]
    public void ATreeItemRepaintsWhenAnEmptyFolderIsFinallyWalked()
    {
        var item = new StorageTreeItemViewModel(Folder() with { IsMeasured = false }, _ => []);
        var raised = new List<string>();
        item.PropertyChanged += (_, args) => raised.Add(args.PropertyName!);

        item.Adopt(Folder());

        Assert.AreEqual("0 B", item.SizeDisplay);
        CollectionAssert.Contains(raised, nameof(StorageTreeItemViewModel.SizeDisplay));
    }

    [TestMethod]
    public async Task AFinishedScanHasWalkedEverythingItReports()
    {
        _fixture.File("a.bin", 4096);
        _fixture.File("sub/b.bin", 4096);
        _fixture.Dir("empty");

        var source = new LiveStorageSnapshotSource();
        StorageSnapshot snapshot = await source.GetSnapshotAsync(
            new StorageSnapshotRequest(_fixture.Root), null, CancellationToken.None);

        Assert.IsTrue(
            snapshot.Nodes.All(node => node.IsMeasured),
            "nothing may still read as pending once the scan has finished");

        StorageNode empty = snapshot.Nodes.Single(node => node.Name == "empty");
        Assert.AreEqual("0 B", empty.SizeDisplay, "a folder we walked and found empty is zero, not unknown");
    }

    [TestMethod]
    public async Task APartialReportsFoldersItHasNotOpenedYetAsUnmeasured()
    {
        // A wide tree with a file in each branch, scanned with a zero interval so a partial is
        // published after every directory. The first partial has walked the root and nothing else.
        for (var i = 0; i < 40; i++)
        {
            _fixture.File($"branch{i:D2}/leaf.bin", 4096);
        }

        var partials = new List<StorageSnapshot>();
        var progress = new Progress<StorageScanProgress>(report =>
        {
            if (report.Partial is StorageSnapshot partial)
            {
                partials.Add(partial);
            }
        });

        var source = new LiveStorageSnapshotSource(
            LiveStorageSnapshotSource.DefaultMaxChildrenPerFolder, TimeSpan.Zero);
        await source.GetSnapshotAsync(
            new StorageSnapshotRequest(_fixture.Root), progress, CancellationToken.None);

        Assert.IsNotEmpty(partials, "a zero interval must publish partials");

        StorageSnapshot first = partials[0];
        Assert.IsTrue(first.Root.IsMeasured, "the root is walked before the first partial is built");
        Assert.IsTrue(
            first.Nodes.Any(node => !node.IsMeasured),
            "the branches the root listed have not been opened yet");
        Assert.IsTrue(
            first.Nodes.Where(node => !node.IsMeasured).All(node => node.SizeDisplay == "—"),
            "and every one of them must say so rather than claim zero");
    }

    [TestMethod]
    public async Task EveryFileIsMeasuredOnTheReFsFallbackPath()
    {
        // The managed enumeration is the ReFS path — a Dev Drive with 64-bit file ids drops to it
        // for every directory. It is also unreachable from a test on NTFS without a seam, which is
        // how it shipped building leaves that claimed to be unmeasured: every file on the volume
        // this tool exists for would have rendered a permanent em dash instead of a size.
        _fixture.File("src/app.dll", 8192);
        _fixture.File("src/nested/data.bin", 4096);
        _fixture.File("top.txt", 1024);

        var source = new LiveStorageSnapshotSource { ForceManagedEnumeration = true };
        StorageSnapshot snapshot = await source.GetSnapshotAsync(
            new StorageSnapshotRequest(_fixture.Root), progress: null, CancellationToken.None);

        Assert.IsGreaterThan(
            0,
            source.LastFastPathFallbackCount,
            "the seam must actually put the scan on the fallback, or this test proves nothing");

        StorageNode[] files = [.. snapshot.Nodes.Where(node => node.Kind == StorageNodeKind.File)];
        Assert.IsNotEmpty(files);
        Assert.IsTrue(
            files.All(file => file.IsMeasured),
            "a file was read off the disk to get here — its size is known");
        Assert.IsFalse(
            files.Any(file => file.SizeDisplay == "—"),
            "so no file may render as unmeasured");
    }

    [TestMethod]
    public async Task TheTwoEnumerationPathsAgreeOnWhatWasMeasured()
    {
        _fixture.File("a/one.bin", 4096);
        _fixture.File("a/b/two.bin", 8192);
        _fixture.Dir("a/empty");

        StorageSnapshot native = await new LiveStorageSnapshotSource().GetSnapshotAsync(
            new StorageSnapshotRequest(_fixture.Root), progress: null, CancellationToken.None);
        StorageSnapshot managed = await new LiveStorageSnapshotSource { ForceManagedEnumeration = true }
            .GetSnapshotAsync(new StorageSnapshotRequest(_fixture.Root), progress: null, CancellationToken.None);

        string[] Unmeasured(StorageSnapshot s) =>
            [.. s.Nodes.Where(n => !n.IsMeasured).Select(n => n.Name).Order()];

        CollectionAssert.AreEqual(
            Unmeasured(native),
            Unmeasured(managed),
            "the fallback is a performance difference, not a correctness one — both paths must " +
            "report the same set of things as measured.");
    }
}
