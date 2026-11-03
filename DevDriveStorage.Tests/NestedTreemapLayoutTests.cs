using DevDriveStorage;

namespace DevDriveStorage.Tests;

[TestClass]
public sealed class NestedTreemapLayoutTests
{
    private const double Epsilon = 0.0001;

    [TestMethod]
    public void CellsStayOnTheCanvasAndSiblingsNeverOverlap()
    {
        Tree tree = Tree.Wide(folders: 6, filesPerFolder: 5);

        IReadOnlyList<NestedTreemapCell> cells =
            NestedTreemapLayout.Calculate(tree.Build(), tree.Root, 800, 500);

        Assert.IsNotEmpty(cells);
        foreach (NestedTreemapCell cell in cells)
        {
            Assert.IsGreaterThanOrEqualTo(-Epsilon, cell.X);
            Assert.IsGreaterThanOrEqualTo(-Epsilon, cell.Y);
            Assert.IsLessThanOrEqualTo(800 + Epsilon, cell.Right);
            Assert.IsLessThanOrEqualTo(500 + Epsilon, cell.Bottom);
            Assert.IsGreaterThanOrEqualTo(NestedTreemapOptions.Default.MinimumCellSide, cell.Width);
            Assert.IsGreaterThanOrEqualTo(NestedTreemapOptions.Default.MinimumCellSide, cell.Height);
        }

        foreach (IGrouping<Guid?, NestedTreemapCell> siblings in cells.GroupBy(cell => cell.ParentId))
        {
            NestedTreemapCell[] group = [.. siblings];
            for (int first = 0; first < group.Length; first++)
            {
                for (int second = first + 1; second < group.Length; second++)
                {
                    Assert.IsFalse(
                        Overlaps(group[first], group[second]),
                        $"{group[first].Name} overlaps {group[second].Name}");
                }
            }
        }
    }

    [TestMethod]
    public void ChildrenSitInsideTheirParentBelowItsHeader()
    {
        Tree tree = Tree.Wide(folders: 4, filesPerFolder: 6);
        NestedTreemapOptions options = NestedTreemapOptions.Default;

        IReadOnlyList<NestedTreemapCell> cells =
            NestedTreemapLayout.Calculate(tree.Build(), tree.Root, 1200, 800);
        Dictionary<Guid, NestedTreemapCell> byId = cells.ToDictionary(cell => cell.Id);

        NestedTreemapCell[] nested = [.. cells.Where(cell => cell.ParentId is not null)];
        Assert.IsNotEmpty(nested, "a roomy canvas should open the top-level folders up");
        foreach (NestedTreemapCell child in nested)
        {
            NestedTreemapCell parent = byId[child.ParentId!.Value];
            Assert.IsTrue(parent.IsContainer);
            Assert.AreEqual(parent.Depth + 1, child.Depth);
            Assert.IsGreaterThanOrEqualTo(parent.X + options.Padding - Epsilon, child.X);
            Assert.IsGreaterThanOrEqualTo(parent.Y + parent.HeaderHeight - Epsilon, child.Y);
            Assert.IsLessThanOrEqualTo(parent.Right - options.Padding + Epsilon, child.Right);
            Assert.IsLessThanOrEqualTo(parent.Bottom - options.Padding + Epsilon, child.Bottom);
        }

        Assert.IsTrue(
            cells.Where(cell => cell.Depth == 0).All(cell => cell.HeaderHeight == options.TopLevelHeaderHeight),
            "every top-level folder here has room to open, and top-level strips are the taller kind");
    }

    [TestMethod]
    public void EmitsParentsBeforeTheirChildren()
    {
        Tree tree = Tree.Wide(folders: 5, filesPerFolder: 5);

        IReadOnlyList<NestedTreemapCell> cells =
            NestedTreemapLayout.Calculate(tree.Build(), tree.Root, 1000, 700);

        var seen = new HashSet<Guid>();
        foreach (NestedTreemapCell cell in cells)
        {
            if (cell.ParentId is Guid parent)
            {
                Assert.Contains(parent, seen, $"{cell.Name} came before its parent");
            }

            seen.Add(cell.Id);
        }
    }

    [TestMethod]
    public void SmallestSiblingsFoldIntoOneOverflowCell()
    {
        var tree = new Tree();
        for (int index = 1; index <= 30; index++)
        {
            tree.File(tree.Root, $"file{index:D2}.bin", index * 1000);
        }

        StorageSnapshot snapshot = tree.Build(rootBytes: 465_000);
        IReadOnlyList<NestedTreemapCell> cells =
            NestedTreemapLayout.Calculate(snapshot, tree.Root, 2000, 2000);

        Assert.HasCount(StorageExplorerViewModel.TreemapItemLimit + 1, cells);
        NestedTreemapCell overflow = cells.Single(cell => cell.Kind == TreemapCellKind.Overflow);
        Assert.AreEqual(6, overflow.FoldedCount);
        Assert.AreEqual(21_000, overflow.SizeBytes, "files 1..6 KB are the six smallest");
        Assert.AreEqual("+6 more", overflow.Name);
        Assert.AreSame(snapshot.Root, overflow.Target);
        Assert.AreEqual(NestedTreemapLayout.OverflowIdOf(tree.Root), overflow.Id);
        Assert.IsFalse(snapshot.Nodes.Any(node => node.Id == overflow.Id));
        Assert.IsFalse(cells.Any(cell => cell.Kind == TreemapCellKind.Unlisted));
        Assert.IsTrue(
            cells.Where(cell => cell.Kind == TreemapCellKind.File).All(cell => cell.SizeBytes >= 7000),
            "only the largest children are drawn individually");
    }

    [TestMethod]
    public void UnaccountedBytesBecomeAFilesBlockWhileScanning()
    {
        var tree = new Tree();
        Guid folder = tree.Folder(tree.Root, "src", 400);
        tree.File(folder, "app.bin", 400);

        StorageSnapshot partial = tree.Build(rootBytes: 1000, SnapshotCompletion.Partial);
        NestedTreemapCell unlisted = NestedTreemapLayout.Calculate(partial, tree.Root, 600, 400)
            .Single(cell => cell.Kind == TreemapCellKind.Unlisted);
        Assert.AreEqual(600, unlisted.SizeBytes);
        Assert.AreEqual("Files", unlisted.Name);
        Assert.AreSame(partial.Root, unlisted.Target);
        Assert.AreEqual(NestedTreemapLayout.UnlistedIdOf(tree.Root), unlisted.Id);
        Assert.AreNotEqual(NestedTreemapLayout.OverflowIdOf(tree.Root), unlisted.Id);

        StorageSnapshot complete = tree.Build(rootBytes: 1000, SnapshotCompletion.Complete);
        NestedTreemapCell other = NestedTreemapLayout.Calculate(complete, tree.Root, 600, 400)
            .Single(cell => cell.Kind == TreemapCellKind.Unlisted);
        Assert.AreEqual("Other", other.Name);
        Assert.AreEqual(unlisted.Id, other.Id, "the synthetic ID is stable across snapshots");
    }

    [TestMethod]
    public void RoundingNoiseAndUnmeasuredFoldersAddNoRemainder()
    {
        var tree = new Tree();
        Guid pending = tree.Folder(tree.Root, "pending", 500, measured: false);
        tree.File(pending, "seen.bin", 100);
        tree.File(tree.Root, "big.bin", 498);

        IReadOnlyList<NestedTreemapCell> cells =
            NestedTreemapLayout.Calculate(tree.Build(rootBytes: 1000), tree.Root, 800, 600);

        Assert.IsFalse(
            cells.Any(cell => cell.Kind == TreemapCellKind.Unlisted),
            "2 bytes of 1000 is rounding, and an unmeasured folder's total proves nothing");
    }

    [TestMethod]
    public void TheCellBudgetKeepsTheTopLevelAndDemotesUnopenedFolders()
    {
        Tree tree = Tree.Wide(folders: 5, filesPerFolder: 5);
        NestedTreemapOptions options = NestedTreemapOptions.Default with { MaxCells = 12 };

        IReadOnlyList<NestedTreemapCell> cells =
            NestedTreemapLayout.Calculate(tree.Build(), tree.Root, 1000, 1000, options);

        NestedTreemapCell[] topLevel = [.. cells.Where(cell => cell.Depth == 0)];
        Assert.HasCount(5, topLevel);
        Assert.IsLessThanOrEqualTo(options.MaxCells, cells.Count);
        Assert.AreEqual(1, topLevel.Count(cell => cell.IsContainer), "only one folder fits in the budget");
        foreach (NestedTreemapCell container in cells.Where(cell => cell.IsContainer))
        {
            Assert.IsTrue(
                cells.Any(cell => cell.ParentId == container.Id),
                $"{container.Name} is drawn as a frame but holds nothing");
        }
    }

    [TestMethod]
    public void NestingStopsAtTheDepthLimit()
    {
        var tree = new Tree();
        Guid parent = tree.Root;
        for (int level = 0; level < 5; level++)
        {
            parent = tree.Folder(parent, $"level{level}", 1000);
        }

        tree.File(parent, "leaf.bin", 1000);
        NestedTreemapOptions options = NestedTreemapOptions.Default with { MaxDepth = 2 };

        IReadOnlyList<NestedTreemapCell> cells =
            NestedTreemapLayout.Calculate(tree.Build(rootBytes: 1000), tree.Root, 1000, 1000, options);

        Assert.HasCount(3, cells);
        NestedTreemapCell deepest = cells.Single(cell => cell.Depth == 2);
        Assert.IsFalse(deepest.IsContainer);
        Assert.AreEqual("level2", deepest.Name);
    }

    [TestMethod]
    public void NestedCellsCarryTheirTopLevelBranch()
    {
        Tree tree = Tree.Deep(folders: 3, subfolders: 3, filesPerFolder: 3);

        IReadOnlyList<NestedTreemapCell> cells =
            NestedTreemapLayout.Calculate(tree.Build(), tree.Root, 1400, 900);
        Dictionary<Guid, NestedTreemapCell> byId = cells.ToDictionary(cell => cell.Id);

        Assert.IsTrue(cells.Any(cell => cell.Depth >= 2), "the canvas is large enough for two levels");
        foreach (NestedTreemapCell cell in cells)
        {
            Guid expected = cell.ParentId is Guid parent ? byId[parent].BranchId : cell.Id;
            Assert.AreEqual(expected, cell.BranchId, cell.Name);
        }
    }

    [TestMethod]
    public void LayoutIsDeterministic()
    {
        Tree tree = Tree.Deep(folders: 4, subfolders: 4, filesPerFolder: 4);
        StorageSnapshot snapshot = tree.Build();

        IReadOnlyList<NestedTreemapCell> first = NestedTreemapLayout.Calculate(snapshot, tree.Root, 900, 600);
        IReadOnlyList<NestedTreemapCell> second = NestedTreemapLayout.Calculate(snapshot, tree.Root, 900, 600);

        CollectionAssert.AreEqual(first.ToList(), second.ToList());
    }

    [TestMethod]
    public void EmptyOrZeroSizedInputsProduceNoCells()
    {
        var tree = new Tree();
        tree.File(tree.Root, "empty.txt", 0);
        StorageSnapshot snapshot = tree.Build(rootBytes: 0);

        Assert.IsEmpty(NestedTreemapLayout.Calculate(snapshot, tree.Root, 800, 600));
        Assert.IsEmpty(NestedTreemapLayout.Calculate(snapshot, Guid.NewGuid(), 800, 600));
        Assert.IsEmpty(NestedTreemapLayout.Calculate(Tree.Wide(2, 2).Build(), tree.Root, 0, 600));
        Assert.IsEmpty(NestedTreemapLayout.Calculate(Tree.Wide(2, 2).Build(), tree.Root, 800, double.NaN));
    }

    [TestMethod]
    public void AHugeTreeStaysWithinTheCellBudget()
    {
        Tree tree = Tree.Deep(folders: 30, subfolders: 30, filesPerFolder: 30);

        IReadOnlyList<NestedTreemapCell> cells =
            NestedTreemapLayout.Calculate(tree.Build(), tree.Root, 2560, 1440);

        Assert.IsLessThanOrEqualTo(NestedTreemapOptions.Default.MaxCells, cells.Count);
        Assert.AreEqual(
            StorageExplorerViewModel.TreemapItemLimit + 1,
            cells.Count(cell => cell.Depth == 0),
            "24 folders plus the overflow for the other six");
    }

    private static bool Overlaps(NestedTreemapCell left, NestedTreemapCell right) =>
        left.X < right.Right - Epsilon &&
        left.Right > right.X + Epsilon &&
        left.Y < right.Bottom - Epsilon &&
        left.Bottom > right.Y + Epsilon;

    private sealed class Tree
    {
        private readonly List<StorageNode> _nodes = [];
        private readonly Dictionary<Guid, string> _paths = [];
        private int _next;

        public Tree()
        {
            Root = NextId();
            _paths[Root] = @"M:\";
        }

        public Guid Root { get; }

        public static Tree Wide(int folders, int filesPerFolder)
        {
            var tree = new Tree();
            for (int folder = 0; folder < folders; folder++)
            {
                long fileBytes = (folders - folder) * 10_000L;
                Guid id = tree.Folder(tree.Root, $"folder{folder:D2}", fileBytes * filesPerFolder);
                for (int file = 0; file < filesPerFolder; file++)
                {
                    tree.File(id, $"file{file:D2}.bin", fileBytes);
                }
            }

            return tree;
        }

        public static Tree Deep(int folders, int subfolders, int filesPerFolder)
        {
            var tree = new Tree();
            for (int folder = 0; folder < folders; folder++)
            {
                long fileBytes = (folders - folder) * 1_000L;
                long subfolderBytes = fileBytes * filesPerFolder;
                Guid id = tree.Folder(tree.Root, $"folder{folder:D2}", subfolderBytes * subfolders);
                for (int subfolder = 0; subfolder < subfolders; subfolder++)
                {
                    Guid child = tree.Folder(id, $"sub{subfolder:D2}", subfolderBytes);
                    for (int file = 0; file < filesPerFolder; file++)
                    {
                        tree.File(child, $"file{file:D2}.bin", fileBytes);
                    }
                }
            }

            return tree;
        }

        public Guid Folder(Guid parent, string name, long bytes, bool measured = true) =>
            Add(parent, name, StorageNodeKind.Folder, bytes, measured);

        public Guid File(Guid parent, string name, long bytes) =>
            Add(parent, name, StorageNodeKind.File, bytes, measured: true);

        public StorageSnapshot Build(
            long? rootBytes = null,
            SnapshotCompletion completion = SnapshotCompletion.Complete)
        {
            long bytes = rootBytes ?? _nodes.Where(node => node.ParentId == Root).Sum(node => node.SizeBytes);
            StorageNode root = StorageTestBuilder.Node(
                Root, null, "Mock drive (M:)", @"M:\", StorageNodeKind.Folder, bytes, _nodes.Count);
            return new StorageSnapshot(
                StorageSnapshot.CurrentSchemaVersion,
                "nested",
                "snapshot-nested",
                Root,
                DateTimeOffset.Parse("2026-08-02T12:00:00Z"),
                completion,
                new ScanCoverage(bytes, bytes, []),
                [root, .. _nodes]);
        }

        private Guid Add(Guid parent, string name, StorageNodeKind kind, long bytes, bool measured)
        {
            Guid id = NextId();
            string path = _paths[parent].TrimEnd('\\') + @"\" + name;
            _paths[id] = path;
            StorageNode node = StorageTestBuilder.Node(id, parent, name, path, kind, bytes) with
            {
                IsMeasured = measured,
            };
            _nodes.Add(node);
            return id;
        }

        private Guid NextId() => Guid.Parse($"30000000-0000-0000-0000-{++_next:D12}");
    }
}
