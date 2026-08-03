using System.Collections.Immutable;
using DevDriveStorage;

namespace DevDriveStorage.Tests;

[TestClass]
public sealed class LiveStorageSnapshotSourceTests
{
    private LiveScanFixture _fixture = null!;

    [TestInitialize]
    public void Init() => _fixture = new LiveScanFixture();

    [TestCleanup]
    public void Cleanup() => _fixture.Dispose();

    private static Task<StorageSnapshot> ScanAsync(
        string root,
        IProgress<StorageScanProgress>? progress = null,
        int maxChildren = LiveStorageSnapshotSource.DefaultMaxChildrenPerFolder,
        TimeSpan? interval = null,
        CancellationToken cancellationToken = default)
    {
        var source = new LiveStorageSnapshotSource(maxChildren, interval);
        return source.GetSnapshotAsync(
            new StorageSnapshotRequest(root),
            progress,
            cancellationToken);
    }

    [TestMethod]
    public async Task KnownTreeScansToExpectedStructureAndTotals()
    {
        _fixture.File("a.bin", 8192);
        _fixture.File("sub/b.bin", 4096);
        _fixture.File("sub/c.bin", 4096);

        StorageSnapshot snapshot = await ScanAsync(_fixture.Root);

        StorageNode root = snapshot.Root;
        Assert.IsNull(root.ParentId);
        Assert.AreEqual(StorageNodeKind.Folder, root.Kind);

        StorageNode[] files = snapshot.Nodes.Where(n => n.Kind == StorageNodeKind.File).ToArray();
        StorageNode[] folders = snapshot.Nodes.Where(n => n.Kind == StorageNodeKind.Folder).ToArray();
        Assert.HasCount(3, files, "three files expected");
        Assert.HasCount(2, folders, "root + sub folder expected");

        // ItemCount is the recursive descendant count: root sees a.bin, sub, b.bin, c.bin.
        Assert.AreEqual(4, root.ItemCount);
        StorageNode sub = folders.Single(f => f.Name == "sub");
        Assert.AreEqual(2, sub.ItemCount);

        // Aggregated allocated bytes must equal the sum of the leaf allocations.
        long fileSum = files.Sum(f => f.SizeBytes);
        Assert.AreEqual(fileSum, root.SizeBytes);
        Assert.IsGreaterThanOrEqualTo(16384, root.SizeBytes, "allocation should cover the written bytes");
    }

    [TestMethod]
    public async Task DeniedSubtreeYieldsPartialCoverageWithPath()
    {
        _fixture.File("visible/keep.bin", 4096);
        string locked = _fixture.Dir("locked");
        _fixture.File("locked/secret.bin", 4096);
        _fixture.DenyListing(locked);

        StorageSnapshot snapshot = await ScanAsync(_fixture.Root);

        Assert.AreEqual(SnapshotCompletion.Partial, snapshot.Completion);
        CollectionAssert.Contains(
            snapshot.Coverage.DeniedPaths.ToArray(),
            locked,
            "the denied directory must be reported, not silently omitted");

        // The denied directory is still present as a node; only its contents are missing.
        Assert.IsFalse(
            snapshot.Nodes.Any(n => n.PhysicalPath.EndsWith("secret.bin", StringComparison.OrdinalIgnoreCase)),
            "contents of a denied directory must not be counted");
    }

    [TestMethod]
    public async Task ReparsePointIsNotFollowed()
    {
        _fixture.File("data/f1.bin", 4096);
        string target = Path.Combine(Path.GetTempPath(), "devdrive-junction-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            for (int i = 0; i < 5; i++)
            {
                using var stream = new FileStream(Path.Combine(target, $"t{i}.bin"), FileMode.Create);
                stream.SetLength(4096);
            }

            _fixture.Junction("link", target);

            StorageSnapshot snapshot = await ScanAsync(_fixture.Root);

            StorageNode root = snapshot.Root;
            // data, f1.bin and link — the five files behind the junction are NOT counted.
            Assert.AreEqual(3, root.ItemCount, "the junction target must not be traversed");

            StorageNode link = snapshot.Nodes.Single(n => n.Name == "link");
            Assert.IsFalse(
                snapshot.Nodes.Any(n => n.ParentId == link.Id),
                "the junction must be a leaf with no emitted children");
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    [TestMethod]
    public async Task CancellationBeforeStartStopsPromptly()
    {
        _fixture.File("a.bin", 4096);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        OperationCanceledException? caught = null;
        try
        {
            await ScanAsync(_fixture.Root, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException exception)
        {
            caught = exception;
        }

        Assert.IsNotNull(caught, "a cancelled token must abort the scan");
    }

    [TestMethod]
    public async Task CancellationDuringScanStopsPromptly()
    {
        for (int i = 0; i < 4000; i++)
        {
            _fixture.File($"bucket{i % 8}/f{i}.bin", 512);
        }

        using var cts = new CancellationTokenSource();
        var progress = new CancelOnReportProgress(cts);

        OperationCanceledException? caught = null;
        try
        {
            // Zero interval makes the scanner report on the first file, which cancels immediately.
            await ScanAsync(_fixture.Root, progress, interval: TimeSpan.Zero, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException exception)
        {
            caught = exception;
        }

        Assert.IsNotNull(caught, "a mid-scan cancellation must abort the scan");
    }

    [TestMethod]
    public async Task ProgressFractionsStayInRangeAndAreMonotonic()
    {
        for (int i = 0; i < 3000; i++)
        {
            _fixture.File($"g{i % 6}/f{i}.bin", 256);
        }

        var progress = new RecordingProgress();
        await ScanAsync(_fixture.Root, progress, interval: TimeSpan.Zero);

        Assert.IsGreaterThan(1, progress.Fractions.Count, "the scan should report progress");
        double previous = -1;
        foreach (double fraction in progress.Fractions)
        {
            Assert.IsGreaterThanOrEqualTo(0, fraction, $"fraction {fraction} out of range");
            Assert.IsLessThanOrEqualTo(1, fraction, $"fraction {fraction} out of range");
            Assert.IsGreaterThanOrEqualTo(previous, fraction, $"fraction {fraction} decreased from {previous}");
            previous = fraction;
        }

        Assert.AreEqual(1, progress.Fractions[^1], "the final report must be complete");
    }

    [TestMethod]
    public async Task SparseFileReportsAllocatedLessThanApparent()
    {
        const long apparent = 4 * 1024 * 1024;
        _fixture.SparseFile("sparse.bin", apparent);

        StorageSnapshot snapshot = await ScanAsync(_fixture.Root);

        StorageNode sparse = snapshot.Nodes.Single(n => n.Name == "sparse.bin");
        Assert.IsTrue(sparse.HasLogicalDifference, "a sparse file must expose a logical/allocated gap");
        Assert.IsNotNull(sparse.LogicalBytes);
        Assert.IsLessThan(
            sparse.LogicalBytes!.Value,
            sparse.SizeBytes,
            $"allocated {sparse.SizeBytes} should be below apparent {sparse.LogicalBytes}");
        Assert.AreEqual(apparent, sparse.LogicalBytes);
    }

    [TestMethod]
    public async Task LargeFolderRollsUpBeyondThreshold()
    {
        for (int i = 0; i < 10; i++)
        {
            _fixture.File($"f{i:D2}.bin", (i + 1) * 4096L);
        }

        StorageSnapshot snapshot = await ScanAsync(_fixture.Root, maxChildren: 4);

        IReadOnlyList<StorageNode> children = snapshot.ChildrenOf(snapshot.RootId);
        Assert.HasCount(4, children, "3 kept children + 1 aggregate");
        Assert.IsTrue(
            children.Any(c => c.Name.Contains("more items", StringComparison.OrdinalIgnoreCase)),
            "an aggregate node should absorb the rolled-up children");

        // The honest total is preserved even though only four rows are emitted.
        Assert.AreEqual(10, snapshot.Root.ItemCount);
    }

    [TestMethod]
    public async Task EmptyFolderScansCleanly()
    {
        StorageSnapshot snapshot = await ScanAsync(_fixture.Root);

        Assert.HasCount(1, snapshot.Nodes);
        Assert.AreEqual(0, snapshot.Root.ItemCount);
        Assert.AreEqual(SnapshotCompletion.Complete, snapshot.Completion);
    }

    [TestMethod]
    public async Task ProviderCorrelationTagsKnownArtifacts()
    {
        _fixture.File("app/node_modules/left-pad/index.js", 1024);
        _fixture.File("disk.vhdx", 8192);

        StorageSnapshot snapshot = await ScanAsync(_fixture.Root);

        StorageNode nodeModules = snapshot.Nodes.Single(n => n.Name == "node_modules");
        Assert.IsNotNull(nodeModules.Provider);
        Assert.AreEqual("npm", nodeModules.Provider!.ProviderName);
        Assert.AreEqual(ProviderAvailability.Stale, nodeModules.Provider.Availability);

        StorageNode vhdx = snapshot.Nodes.Single(n => n.Name == "disk.vhdx");
        Assert.IsNotNull(vhdx.Provider);
        Assert.AreEqual("Virtual disk", vhdx.Provider!.ProviderName);
    }

    private sealed class RecordingProgress : IProgress<StorageScanProgress>
    {
        private readonly Lock _gate = new();

        public List<double> Fractions { get; } = [];

        public void Report(StorageScanProgress value)
        {
            lock (_gate)
            {
                Fractions.Add(value.Fraction);
            }
        }
    }

    private sealed class CancelOnReportProgress(CancellationTokenSource source)
        : IProgress<StorageScanProgress>
    {
        public void Report(StorageScanProgress value) => source.Cancel();
    }
}
