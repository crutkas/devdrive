using System.Diagnostics;
using DevDriveStorage;

namespace DevDriveStorage.Tests;

/// <summary>
/// Guards the shape of the work the explorer does when a snapshot is the size of a real volume
/// rather than a hand-written fixture. Every regression these cover looked correct on a
/// three-node mock and hung the UI thread on a live scan.
/// </summary>
[TestClass]
public sealed class LargeSnapshotScalabilityTests
{
    private const int FolderCount = 4_000;
    private const int FilesPerFolder = 6;

    /// <summary>
    /// Child and descendant lookups must not rescan the whole snapshot. The previous
    /// implementation filtered every node per call, so loading a snapshot cost roughly node count
    /// times folder count and never completed on a real volume.
    /// </summary>
    [TestMethod]
    public async Task LoadingALargeSnapshotStaysInteractive()
    {
        StorageSnapshot snapshot = BuildWideSnapshot();
        var viewModel = new StorageExplorerViewModel(new StaticSource(snapshot));

        var watch = Stopwatch.StartNew();
        await viewModel.RefreshAsync();
        watch.Stop();

        Assert.AreEqual(ExplorerScanState.Completed, viewModel.ScanState);

        // Generous by three orders of magnitude against the quadratic behaviour this replaces,
        // so the assertion fails on an algorithmic regression rather than on a slow machine.
        Assert.IsLessThan(
            TimeSpan.FromSeconds(10),
            watch.Elapsed,
            $"loading {snapshot.Nodes.Length:N0} nodes took {watch.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// The folder tree must materialise lazily. Creating a view model for every folder up front is
    /// what turned a completed scan into a frozen window.
    /// </summary>
    [TestMethod]
    public async Task TheFolderTreeMaterialisesOnlyWhatIsAskedFor()
    {
        StorageSnapshot snapshot = BuildDeepSnapshot(depth: 400);
        var viewModel = new StorageExplorerViewModel(new StaticSource(snapshot));

        await viewModel.RefreshAsync();

        StorageTreeItemViewModel root = viewModel.TreeRoots.Single();
        Assert.IsFalse(
            root.Children.Single().HasMaterialisedChildren,
            "a grandchild level must not be built until something asks for it");
    }

    /// <summary>The index must preserve the display order the previous sort produced.</summary>
    [TestMethod]
    public void ChildrenKeepFoldersFirstThenNameOrder()
    {
        Guid rootId = StorageTestBuilder.RootId;
        Guid zebraFolder = Guid.NewGuid();
        Guid alphaFolder = Guid.NewGuid();
        Guid betaFile = Guid.NewGuid();

        StorageSnapshot snapshot = StorageTestBuilder.Snapshot(nodes:
        [
            StorageTestBuilder.Node(rootId, null, "root", @"M:\", StorageNodeKind.Folder, 30, 3),
            StorageTestBuilder.Node(betaFile, rootId, "beta.bin", @"M:\beta.bin", StorageNodeKind.File, 10),
            StorageTestBuilder.Node(zebraFolder, rootId, "zebra", @"M:\zebra", StorageNodeKind.Folder, 10, 0),
            StorageTestBuilder.Node(alphaFolder, rootId, "alpha", @"M:\alpha", StorageNodeKind.Folder, 10, 0),
        ]);

        string[] order = [.. snapshot.ChildrenOf(rootId).Select(node => node.Name)];

        CollectionAssert.AreEqual(new[] { "alpha", "zebra", "beta.bin" }, order);
    }

    /// <summary>A subtree walk must find exactly the subtree, not the ancestors around it.</summary>
    [TestMethod]
    public void DescendantsReturnTheWholeSubtreeAndNothingElse()
    {
        StorageSnapshot snapshot = BuildWideSnapshot();
        StorageNode branch = snapshot.ChildrenOf(snapshot.RootId)[0];

        Assert.HasCount(FilesPerFolder, snapshot.DescendantsOf(branch.Id));
        Assert.HasCount(snapshot.Nodes.Length - 1, snapshot.DescendantsOf(snapshot.RootId));
    }

    /// <summary>Many sibling folders, each holding files — the shape of a package cache.</summary>
    private static StorageSnapshot BuildWideSnapshot()
    {
        Guid rootId = StorageTestBuilder.RootId;
        var nodes = new List<StorageNode>(1 + (FolderCount * (1 + FilesPerFolder)))
        {
            StorageTestBuilder.Node(rootId, null, "Big (B:)", @"B:\", StorageNodeKind.Folder, 0, 0),
        };

        for (int folder = 0; folder < FolderCount; folder++)
        {
            Guid folderId = Guid.NewGuid();
            nodes.Add(StorageTestBuilder.Node(
                folderId, rootId, $"pkg{folder:D5}", $@"B:\pkg{folder:D5}",
                StorageNodeKind.Folder, FilesPerFolder * 100, FilesPerFolder));

            for (int file = 0; file < FilesPerFolder; file++)
            {
                nodes.Add(StorageTestBuilder.Node(
                    Guid.NewGuid(), folderId, $"f{file}.bin",
                    $@"B:\pkg{folder:D5}\f{file}.bin", StorageNodeKind.File, 100));
            }
        }

        return StorageTestBuilder.Snapshot(nodes: nodes);
    }

    /// <summary>A single deep chain — the shape that punishes eager tree construction.</summary>
    private static StorageSnapshot BuildDeepSnapshot(int depth)
    {
        Guid rootId = StorageTestBuilder.RootId;
        var nodes = new List<StorageNode>(depth + 1)
        {
            StorageTestBuilder.Node(rootId, null, "Deep (D:)", @"D:\", StorageNodeKind.Folder, 0, 0),
        };

        Guid parentId = rootId;
        string path = @"D:";
        for (int level = 0; level < depth; level++)
        {
            Guid id = Guid.NewGuid();
            path += $@"\l{level}";
            nodes.Add(StorageTestBuilder.Node(
                id, parentId, $"l{level}", path, StorageNodeKind.Folder, 100, 1));
            parentId = id;
        }

        return StorageTestBuilder.Snapshot(nodes: nodes);
    }

    private sealed class StaticSource(StorageSnapshot snapshot) : IStorageSnapshotSource
    {
        public Task<StorageSnapshot> GetSnapshotAsync(
            StorageSnapshotRequest request,
            IProgress<StorageScanProgress>? progress,
            CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}
