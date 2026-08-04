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
    public async Task StreamedPartialsCarryUsableSnapshotsAndNeverDoubleCount()
    {
        // A tree wide enough that the walk crosses several partial-emission points.
        for (int i = 0; i < 12; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                _fixture.File($"d{i}/f{j}.bin", 4096);
            }
        }

        var reports = new List<StorageScanProgress>();
        StorageSnapshot final = await ScanAsync(
            _fixture.Root,
            new SyncProgress(reports.Add),
            interval: TimeSpan.Zero);

        StorageScanProgress[] partials = reports.Where(r => r.Partial is not null).ToArray();
        Assert.IsNotEmpty(partials, "a zero interval must stream partial snapshots");

        foreach (StorageScanProgress report in partials)
        {
            StorageSnapshot partial = report.Partial!;

            // The whole point: a partial has to be a real snapshot the room can render.
            Assert.AreEqual(SnapshotCompletion.Partial, partial.Completion);
            Assert.AreEqual(final.Root.PhysicalPath, partial.Root.PhysicalPath);
            Assert.IsLessThanOrEqualTo(final.Root.SizeBytes, partial.Root.SizeBytes,
                "a partial can never exceed the finished scan — that would mean rollup double-counting");

            // Partials carry folders only, so the room can keep updating on a large volume without
            // each tick costing the whole walk. Folder totals are still full rollups.
            Assert.IsFalse(
                partial.Nodes.Any(node => node.Kind == StorageNodeKind.File),
                "a streamed partial must not carry file nodes");

            foreach (StorageNode folder in partial.Nodes.Where(n => n.Kind == StorageNodeKind.Folder))
            {
                long children = partial.ChildrenOf(folder.Id).Sum(child => child.SizeBytes);
                Assert.IsLessThanOrEqualTo(folder.SizeBytes, children,
                    $"'{folder.Name}' holds less than its subfolders, which means it double-counted");
            }
        }

        // Sizes must climb toward the answer, never overshoot and settle back.
        long[] sizes = partials.Select(r => r.Partial!.Root.SizeBytes).ToArray();
        for (int i = 1; i < sizes.Length; i++)
        {
            Assert.IsGreaterThanOrEqualTo(sizes[i - 1], sizes[i], "partial totals must be monotonic");
        }
    }

    [TestMethod]
    public async Task NodeIdentityIsStableAcrossPartialsAndRescans()
    {
        _fixture.File("keep/a.bin", 4096);
        _fixture.File("keep/nested/b.bin", 4096);

        var reports = new List<StorageScanProgress>();
        StorageSnapshot first = await ScanAsync(
            _fixture.Root,
            new SyncProgress(reports.Add),
            interval: TimeSpan.Zero);
        StorageSnapshot second = await ScanAsync(_fixture.Root);

        // Selection, scope and expanded folders are all restored by id. If ids moved between
        // emissions the room would throw the user back to the root on every update.
        Assert.AreEqual(first.Root.Id, second.Root.Id, "the same path must keep the same id across scans");

        StorageNode nested = first.Nodes.Single(n => n.Name == "nested");
        Assert.AreEqual(
            nested.Id,
            second.Nodes.Single(n => n.Name == "nested").Id,
            "a folder's id must not depend on when it was scanned");

        foreach (StorageSnapshot partial in reports.Where(r => r.Partial is not null).Select(r => r.Partial!))
        {
            Assert.AreEqual(first.Root.Id, partial.Root.Id, "partials must share the final scan's ids");
        }

        Assert.AreEqual(
            first.Nodes.Length,
            first.Nodes.Select(n => n.Id).Distinct().Count(),
            "hashing paths must still produce unique ids");
    }

    /// <summary>Applies reports inline so assertions see them in walk order.</summary>
    private sealed class SyncProgress(Action<StorageScanProgress> onReport)
        : IProgress<StorageScanProgress>
    {
        public void Report(StorageScanProgress value) => onReport(value);
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
    public async Task ClusterSlackIsCapturedInAllocatedSize()
    {
        // 5000 is not a multiple of any Windows cluster size (512/4K/8K/16K/32K/64K), so a
        // dense file of this length is guaranteed to round up on disk. GetCompressedFileSizeW
        // (the old path) returned exactly the apparent length and lost this slack.
        const long apparent = 5000;
        _fixture.File("dense.bin", apparent);

        StorageSnapshot snapshot = await ScanAsync(_fixture.Root);

        StorageNode dense = snapshot.Nodes.Single(n => n.Name == "dense.bin");
        Assert.IsGreaterThan(
            apparent,
            dense.SizeBytes,
            $"allocated {dense.SizeBytes} must round up past apparent {apparent} to capture cluster slack");

        // Sub-cluster slack is ordinary rounding, not the sparse/compression signal, so it must
        // not light up the logical-difference UI.
        Assert.IsFalse(
            dense.HasLogicalDifference,
            "cluster slack (allocated > apparent) must stay hidden");
    }

    [TestMethod]
    public async Task SparseFileReportsNearZeroAllocated()
    {
        // The regression that matters most: an unwritten sparse file occupies ~nothing on disk
        // even though its apparent length is large. AllocationSize must still report that.
        const long apparent = 8 * 1024 * 1024;
        _fixture.SparseFile("empty.sparse", apparent);

        StorageSnapshot snapshot = await ScanAsync(_fixture.Root);

        StorageNode sparse = snapshot.Nodes.Single(n => n.Name == "empty.sparse");
        Assert.IsLessThanOrEqualTo(
            64 * 1024L,
            sparse.SizeBytes,
            $"an empty {apparent}-byte sparse file should allocate ~0 on disk, got {sparse.SizeBytes}");
        Assert.AreEqual(apparent, sparse.LogicalBytes, "the apparent length must still be reported");
    }

    [TestMethod]
    public async Task SnapshotAggregatesRecoverClusterSlack()
    {
        // Every dense file rounds 5000 apparent bytes up to a full cluster on disk. That per-file
        // slack is folded into SizeBytes and (correctly) hidden per row, so the only way a caller
        // can recover it is the snapshot-level aggregate.
        const int fileCount = 8;
        const long apparent = 5000;
        for (int i = 0; i < fileCount; i++)
        {
            _fixture.File($"dense{i:D2}.bin", apparent);
        }

        StorageSnapshot snapshot = await ScanAsync(_fixture.Root);
        ScanCoverage coverage = snapshot.Coverage;

        Assert.IsNotNull(coverage.TotalAllocatedBytes, "live scans must publish the allocated aggregate");
        Assert.IsNotNull(coverage.TotalApparentBytes, "live scans must publish the apparent aggregate");

        // The allocated aggregate is the honest on-disk total and matches the root's rolled-up size.
        Assert.AreEqual(snapshot.Root.SizeBytes, coverage.TotalAllocatedBytes!.Value);
        Assert.AreEqual(fileCount * apparent, coverage.TotalApparentBytes!.Value);

        // Cluster slack is positive (allocated > apparent) and is exactly what was discarded before.
        Assert.IsNotNull(coverage.AllocatedMinusApparentBytes);
        Assert.IsGreaterThan(
            0,
            coverage.AllocatedMinusApparentBytes!.Value,
            "cluster slack across the tree must be recoverable as a positive aggregate delta");
        Assert.AreEqual(
            coverage.TotalAllocatedBytes!.Value - coverage.TotalApparentBytes!.Value,
            coverage.AllocatedMinusApparentBytes!.Value);

        // Recovering the aggregate must not make any individual row noisy.
        Assert.IsFalse(
            snapshot.Nodes.Any(n => n is { Kind: StorageNodeKind.File } && n.HasLogicalDifference),
            "sub-cluster slack must stay hidden per row");
    }

    [TestMethod]
    public async Task SnapshotAggregatesReflectSparseSavings()
    {
        const long denseApparent = 5000;
        const long sparseApparent = 8 * 1024 * 1024;
        _fixture.File("dense.bin", denseApparent);
        _fixture.SparseFile("empty.sparse", sparseApparent);

        StorageSnapshot snapshot = await ScanAsync(_fixture.Root);
        ScanCoverage coverage = snapshot.Coverage;

        Assert.AreEqual(snapshot.Root.SizeBytes, coverage.TotalAllocatedBytes!.Value);
        Assert.AreEqual(denseApparent + sparseApparent, coverage.TotalApparentBytes!.Value);

        // The sparse file's near-zero allocation dwarfs the dense file's cluster slack, so the
        // aggregate delta goes negative: apparent size on this tree exceeds real disk usage.
        Assert.IsLessThan(
            0,
            coverage.AllocatedMinusApparentBytes!.Value,
            "sparse savings must dominate the aggregate and read as apparent > allocated");
    }

    [TestMethod]
    public async Task MockCoverageLeavesAggregatesUnset()
    {
        // The aggregate is a live-scan measurement; the mock path never computes it and must
        // publish null (rather than a misleading zero) so callers can tell "not measured" apart.
        var coverage = new ScanCoverage(10, 10, []);
        Assert.IsNull(coverage.TotalAllocatedBytes);
        Assert.IsNull(coverage.TotalApparentBytes);
        Assert.IsNull(coverage.AllocatedMinusApparentBytes);
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task NormalTreeScansEntirelyOnTheFastPath()
    {
        // A readable NTFS tree must be served entirely by the per-directory fast path. A non-zero
        // fallback count here would mean the fast enumeration silently degraded to the slow managed
        // path (the ReFS 64-bit-FileId failure mode) — this asserts that stays observable and zero.
        _fixture.File("a\\one.bin", 4096);
        _fixture.File("a\\b\\two.bin", 8192);
        _fixture.File("c\\three.bin", 1234);

        var source = new LiveStorageSnapshotSource();
        StorageSnapshot snapshot = await source.GetSnapshotAsync(
            new StorageSnapshotRequest(_fixture.Root), null, CancellationToken.None);

        Assert.AreEqual(SnapshotCompletion.Complete, snapshot.Completion);
        Assert.AreEqual(
            0,
            source.LastFastPathFallbackCount,
            "a readable tree must never fall back from the fast enumeration path");
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
