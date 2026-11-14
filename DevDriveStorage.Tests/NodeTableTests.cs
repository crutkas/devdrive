using System.Diagnostics;
using System.Runtime.ExceptionServices;
using DevDriveStorage;

namespace DevDriveStorage.Tests;

/// <summary>
/// A snapshot keeps its nodes in a table of a few large arrays and makes a node only when asked.
/// These guard what that must not cost: a node read while its table is still being written, ids
/// that outlive a rescan, nodes that read back as they went in, and a footprint of a few dozen
/// arrays at well under a hundred bytes a node.
/// </summary>
[TestClass]
public sealed class NodeTableTests
{
    [TestMethod]
    public void AReaderSeesOnlyCompleteNodesWhileTheWriterAppends()
    {
        // Past four 64K-node segments, so the reader crosses every kind of growth: the first
        // segment doubling, whole new segments, and the array that holds them.
        const int NodeCount = 300_000;
        var watch = Stopwatch.StartNew();
        int rounds = 0;
        long verified = 0;
        do
        {
            var table = new NodeTable();
            using var finished = new ManualResetEventSlim();
            ExceptionDispatchInfo? failure = null;
            long read = 0;
            var reader = new Thread(() =>
            {
                try
                {
                    read = ReadUntilFinished(table, finished);
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            });

            reader.Start();
            for (int i = 0; i < NodeCount; i++)
            {
                Write(table, i);
            }

            finished.Set();
            reader.Join();
            failure?.Throw();
            verified += read;
            rounds++;
        }
        while (watch.ElapsedMilliseconds < 400);

        Assert.AreEqual((long)NodeCount * rounds, verified, "the reader must have checked every node of every round");
    }

    [TestMethod]
    public async Task ASelectionSavedByIdOrByPathIsFoundAgainAfterARescan()
    {
        using var fixture = new LiveScanFixture();
        fixture.File(@"src\app\main.cs", 4096);
        fixture.File(@"src\app\résumé.txt", 4096);
        fixture.File(@"docs\readme.md", 4096);
        fixture.Dir("empty");

        StorageSnapshot first = await new LiveStorageSnapshotSource()
            .GetSnapshotAsync(new StorageSnapshotRequest(fixture.Root), null, CancellationToken.None);
        StorageSnapshot second = await new LiveStorageSnapshotSource()
            .GetSnapshotAsync(new StorageSnapshotRequest(fixture.Root), null, CancellationToken.None);

        Assert.AreEqual(8, first.Nodes.Count, "the root, three folders, a subfolder and three files");
        foreach (StorageNode saved in first.Nodes)
        {
            StorageNode? byId = second.Find(saved.Id);
            Assert.IsNotNull(byId, $"{saved.PhysicalPath} must keep its id across scans");
            Assert.AreEqual(saved.PhysicalPath, byId.PhysicalPath);
            Assert.AreEqual(saved.Name, byId.Name);

            // Ids are derived from paths, so a selection saved as a path leads to the same node.
            Assert.AreEqual(
                saved.Id,
                second.Find(LiveStorageSnapshotSource.StableId(saved.PhysicalPath))?.Id,
                $"{saved.PhysicalPath} must be found again by its path");
        }
    }

    [TestMethod]
    public void NodesReadBackAsTheyWentIn()
    {
        Guid root = StorageTestBuilder.RootId;
        Guid folder = Guid.NewGuid();
        const string Folder = @"M:\非常に長いフォルダー名";
        string longName = new('n', 5_000);
        var provider = new StorageProviderContext("WSL", "Ubuntu", "test", ProviderAvailability.Available, null);
        StorageNode[] nodes =
        [
            StorageTestBuilder.Node(root, null, "Mock drive (M:)", @"M:\", StorageNodeKind.Folder, 900, 9),
            StorageTestBuilder.Node(folder, root, "非常に長いフォルダー名", Folder, StorageNodeKind.Folder, 500, 3) with
            {
                IsMeasured = false,
            },
            StorageTestBuilder.Node(Guid.NewGuid(), folder, "résumé-🚀.bin", Folder + @"\résumé-🚀.bin", StorageNodeKind.File, 100, provider: provider),
            new StorageNode(Guid.NewGuid(), folder, "disk.vhdx", Folder + @"\disk.vhdx", StorageNodeKind.File, 300, 0,
                DateTimeOffset.Parse("2026-08-02T12:00:00+02:00"), null, 4096),
            StorageTestBuilder.Node(Guid.NewGuid(), folder, "\u2026 and 12 more items", StorageNode.RollupPath(Folder, 12),
                StorageNodeKind.Folder, 100, 12),
            StorageTestBuilder.Node(Guid.NewGuid(), root, "Zeta", @"M:\Zeta", StorageNodeKind.Folder, 0),
            StorageTestBuilder.Node(Guid.NewGuid(), root, "alpha.txt", @"M:\alpha.txt", StorageNodeKind.File, 200),
            StorageTestBuilder.Node(Guid.NewGuid(), root, "Ärger.txt", @"M:\Ärger.txt", StorageNodeKind.File, 100),
            StorageTestBuilder.Node(Guid.NewGuid(), root, "beta.txt", @"M:\beta.txt", StorageNodeKind.File, 100),
            StorageTestBuilder.Node(Guid.NewGuid(), root, longName, @"M:\" + longName, StorageNodeKind.File, 100),
            StorageTestBuilder.Node(Guid.NewGuid(), root, "moved.bin", @"M:\elsewhere\moved.bin", StorageNodeKind.File, 0),
        ];

        StorageSnapshot snapshot = StorageTestBuilder.Snapshot(nodes: nodes);

        foreach (StorageNode node in nodes)
        {
            StorageNode? read = snapshot.Find(node.Id);
            Assert.AreEqual(node, read, $"'{node.Name}' must read back as it went in");
            Assert.AreEqual(node.PhysicalPath, read!.PhysicalPath);
            Assert.AreEqual(node.IsRollup, read.IsRollup);
        }

        CollectionAssert.AreEqual(nodes, snapshot.Nodes.ToArray(), "every node, in the order they went in");
        string[] expected =
        [
            .. nodes.Where(node => node.ParentId == root)
                .OrderBy(node => node.Kind == StorageNodeKind.Folder ? 0 : 1)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .Select(node => node.Name),
        ];
        CollectionAssert.AreEqual(expected, snapshot.ChildrenOf(root).Select(node => node.Name).ToArray());
    }

    [TestMethod]
    public void LargestFilesKeepsWhatTheNodesThemselvesWouldSelect()
    {
        Guid root = StorageTestBuilder.RootId;
        Guid src = Guid.NewGuid();
        Guid app = Guid.NewGuid();
        StorageSnapshot snapshot = StorageTestBuilder.Snapshot(nodes:
        [
            StorageTestBuilder.Node(root, null, "Mock drive (M:)", @"M:\", StorageNodeKind.Folder, 0, 9),
            StorageTestBuilder.Node(src, root, "src", @"M:\src", StorageNodeKind.Folder, 0, 6),
            StorageTestBuilder.Node(app, src, "app", @"M:\src\app", StorageNodeKind.Folder, 0, 3),
            StorageTestBuilder.Node(Guid.NewGuid(), app, "main.bin", @"M:\src\app\main.bin", StorageNodeKind.File, 70),
            StorageTestBuilder.Node(Guid.NewGuid(), app, "Ärger.log", @"M:\src\app\Ärger.log", StorageNodeKind.File, 60),
            StorageTestBuilder.Node(Guid.NewGuid(), app, "tiny.bin", @"M:\src\app\tiny.bin", StorageNodeKind.File, 10),
            StorageTestBuilder.Node(Guid.NewGuid(), src, "lib.bin", @"M:\src\lib.bin", StorageNodeKind.File, 50),
            StorageTestBuilder.Node(Guid.NewGuid(), src, "notes.txt", @"M:\src\notes.txt", StorageNodeKind.File, 40),
            StorageTestBuilder.Node(Guid.NewGuid(), root, "readme.md", @"M:\readme.md", StorageNodeKind.File, 30),
            StorageTestBuilder.Node(Guid.NewGuid(), root, "stray.bin", @"M:\other\stray.bin", StorageNodeKind.File, 20),
        ]);

        foreach (string? search in new[] { null, "bin", "SRC", @"app\m", "ärger", "other", "nothing" })
        {
            foreach (int limit in new[] { 1, 3, 100 })
            {
                Guid[] expected =
                [
                    .. snapshot.DescendantsOf(root)
                        .Where(node => node.Kind == StorageNodeKind.File)
                        .Where(node => search is null ||
                            node.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            node.PhysicalPath.Contains(search, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(node => node.SizeBytes)
                        .Take(limit)
                        .Select(node => node.Id)
                        .Order(),
                ];

                CollectionAssert.AreEqual(
                    expected,
                    snapshot.LargestFiles(root, limit, search).Select(node => node.Id).Order().ToArray(),
                    $"search '{search}', limit {limit}");
            }
        }
    }

    [TestMethod]
    public void SiblingsThatShareANameAreRejectedInALargeTableToo()
    {
        // Large enough that the table orders its sibling groups in parallel.
        const int Folders = 10_000;
        var table = new NodeTable();
        table.Add(Guid.NewGuid(), -1, "Big (B:)", StorageNodeKind.Folder, 0, 0, 0, true, ownPath: @"B:\");
        for (int folder = 0; folder < Folders; folder++)
        {
            int index = table.Add(Guid.NewGuid(), 0, $"f{folder:D5}", StorageNodeKind.Folder, 0, 0, 0, true);
            for (int file = 0; file < 8; file++)
            {
                string name = folder == 7_777 && file == 5 ? "Name3.bin" : $"name{file}.bin";
                table.Add(Guid.NewGuid(), index, name, StorageNodeKind.File, 0, 0, 0, true);
            }
        }

        var exception = Assert.ThrowsExactly<StorageSnapshotValidationException>(() => table.Seal(rootIndex: 0));
        Assert.AreEqual(@"Duplicate physical path: B:\f07777\Name3.bin.", exception.Message);
    }

    [TestMethod]
    public void ALargeSnapshotIsAFewArraysAtAFewBytesANode()
    {
        const int Folders = 20_000;
        const int FilesPerFolder = 9;
        const int Count = 1 + (Folders * (1 + FilesPerFolder));

        // A scan's ids and names exist before its snapshot does, so they are made up front and what
        // is measured is the snapshot alone.
        var ids = new Guid[Count];
        var names = new string[Count];
        for (int i = 0; i < Count; i++)
        {
            ids[i] = Guid.NewGuid();
        }

        names[0] = "Big (B:)";
        for (int folder = 0, i = 1; folder < Folders; folder++)
        {
            names[i++] = $"package-{folder:D5}";
            for (int file = 0; file < FilesPerFolder; file++)
            {
                names[i++] = $"lib{file}.{folder:D5}.dll";
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        var table = new NodeTable();
        table.Add(ids[0], -1, names[0], StorageNodeKind.Folder, 0, Count - 1, 0, isMeasured: true, ownPath: @"B:\");
        for (int i = 1; i < Count;)
        {
            int folder = table.Add(ids[i], 0, names[i], StorageNodeKind.Folder, FilesPerFolder * 4096L, FilesPerFolder, 0, true);
            i++;
            for (int file = 0; file < FilesPerFolder; file++, i++)
            {
                table.Add(ids[i], folder, names[i], StorageNodeKind.File, 4096, 0, 0, true);
            }
        }

        table.Seal(rootIndex: 0);
        var snapshot = new StorageSnapshot(
            StorageSnapshot.CurrentSchemaVersion,
            "big",
            "big-1",
            DateTimeOffset.UnixEpoch,
            SnapshotCompletion.Complete,
            new ScanCoverage(0, 0, []),
            table);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        double held = (double)table.FootprintBytes / Count;
        double churned = (double)allocated / Count;
        Assert.IsLessThan(90, held, $"the snapshot holds {held:F1} bytes a node");
        Assert.IsLessThan(150, churned, $"building it allocated {churned:F1} bytes a node");
        Assert.IsLessThan(40, table.ArrayCount, "a full collection visits a few dozen arrays, not an object per node");
        Assert.AreEqual(Count, snapshot.Nodes.Count);
        Assert.HasCount(FilesPerFolder, snapshot.ChildrenOf(ids[1]));
        Assert.AreEqual(@"B:\package-00000\lib0.00000.dll", snapshot.Find(ids[2])!.PhysicalPath);
    }

    private static Guid IdOf(int i) => new(i + 1, 0x5eed, 0x7ab1, 1, 2, 3, 4, 5, 6, 7, 8);

    private static int ParentOf(int i) => i == 0 ? -1 : (i - 1) / 2;

    private static StorageNodeKind KindOf(int i) => i % 3 == 2 ? StorageNodeKind.File : StorageNodeKind.Folder;

    private static string NameOf(int i) => i % 7 == 3
        ? $"résumé-{i}"
        : i % 35_000 == 5 ? new string('x', 5_000) + i : $"n{i}";

    private static string? OwnPathOf(int i) => i == 0 ? @"T:\" : i % 97 == 0 ? $@"T:\own\{i}" : null;

    private static string ExpectedPath(int i) => OwnPathOf(i) ?? Path.Combine(ExpectedPath(ParentOf(i)), NameOf(i));

    private static void Write(NodeTable table, int i) =>
        table.Add(
            IdOf(i),
            ParentOf(i),
            NameOf(i),
            KindOf(i),
            i * 3L,
            KindOf(i) == StorageNodeKind.Folder ? i % 11 : 0,
            1_000_000L + (i * 7L),
            isMeasured: i % 2 == 0,
            logicalBytes: i % 13 == 0 ? i * 5L : null,
            provider: i % 101 == 0
                ? new StorageProviderContext($"provider {i}", null, null, ProviderAvailability.Stale, null)
                : null,
            ownPath: OwnPathOf(i));

    /// <summary>Checks every node as it is published, and rereads old ones while the segments behind them grow.</summary>
    private static long ReadUntilFinished(NodeTable table, ManualResetEventSlim finished)
    {
        var random = new Random(7);
        int next = 0;
        while (true)
        {
            bool done = finished.IsSet;
            int count = table.Count;
            for (; next < count; next++)
            {
                Check(table, next);
            }

            for (int k = 0; k < 32 && count > 0; k++)
            {
                Check(table, random.Next(count));
            }

            if (done)
            {
                return next;
            }
        }
    }

    private static void Check(NodeTable table, int i)
    {
        ref readonly NodeRecord node = ref table[i];
        Expect(node.Id == IdOf(i), i, "id");
        Expect(node.Parent == ParentOf(i), i, "parent");
        Expect(node.SizeBytes == i * 3L, i, "size");
        Expect(node.ModifiedTicks == 1_000_000L + (i * 7L), i, "modified time");
        Expect(node.IsFolder == (KindOf(i) == StorageNodeKind.Folder), i, "kind");
        Expect(node.ItemCount == (node.IsFolder ? i % 11 : 0), i, "item count");
        Expect(node.Has(NodeFlags.Measured) == (i % 2 == 0), i, "measured");
        Expect(table.NameOf(i) == NameOf(i), i, "name");
        Expect(table.LogicalOf(i) == (i % 13 == 0 ? (long?)(i * 5L) : null), i, "apparent size");
        Expect(table.ProviderOf(i)?.ProviderName == (i % 101 == 0 ? $"provider {i}" : null), i, "provider");
        Expect(table.OwnPathOf(i) == OwnPathOf(i), i, "own path");
        if (i % 1_000 == 1)
        {
            Expect(table.PathOf(i) == ExpectedPath(i), i, "path");
        }
    }

    private static void Expect(bool condition, int node, string what)
    {
        if (!condition)
        {
            throw new AssertFailedException($"node {node}: {what} read back wrong");
        }
    }
}
