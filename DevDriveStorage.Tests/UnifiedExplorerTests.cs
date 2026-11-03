using System.Collections.Specialized;
using DevDriveStorage;

namespace DevDriveStorage.Tests;

[TestClass]
public sealed class UnifiedExplorerTests
{
    private static readonly Guid OtherId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid EmptyId = Guid.Parse("40000000-0000-0000-0000-000000000002");

    [TestMethod]
    public async Task OnlyRenderedTreemapItemsHaveSwatches()
    {
        var nodes = Enumerable.Range(0, 30).Select(i => StorageTestBuilder.Node(
            Guid.NewGuid(), StorageTestBuilder.RootId, $"file{i:D2}", $@"M:\file{i:D2}",
            StorageNodeKind.File, 100 - i)).ToList();
        nodes.Add(StorageTestBuilder.Node(StorageTestBuilder.RootId, null, "root", @"M:\",
            StorageNodeKind.Folder, nodes.Sum(node => node.SizeBytes), 30));
        var vm = new StorageExplorerViewModel(new Source(StorageTestBuilder.Snapshot(nodes: nodes)));
        await vm.LoadScenarioAsync("test");

        var children = vm.TreeRoots.Single().Children;
        Assert.AreEqual(StorageExplorerViewModel.TreemapItemLimit, children.Count(item => item.ColorIndex >= 0));
        Assert.IsTrue(children.Skip(StorageExplorerViewModel.TreemapItemLimit).All(item => item.ColorIndex == -1));
        foreach (var item in children.Take(StorageExplorerViewModel.TreemapItemLimit))
        {
            Assert.AreEqual(vm.VisibleItems.Single(row => row.Id == item.Id).ColorIndex, item.ColorIndex);
        }
    }

    [TestMethod]
    public async Task BindingCollapsedBranchesDoesNotMaterializeFiles()
    {
        var vm = new StorageExplorerViewModel(new Source(Snapshot()));
        await vm.LoadScenarioAsync("test");
        var folder = vm.TreeRoots.Single().Children.Single(item => item.Id == StorageTestBuilder.FolderId);
        Assert.IsTrue(folder.HasUnrealizedChildren);
        Assert.IsEmpty(folder.MaterializedChildren);
        Assert.IsFalse(folder.HasMaterialisedChildren);

        folder.IsExpanded = true;

        Assert.IsFalse(folder.HasUnrealizedChildren);
        var file = folder.MaterializedChildren.Single();
        Assert.AreEqual(StorageTestBuilder.FileId, file.Id);
        Assert.IsFalse(file.IsFolder);
        Assert.IsFalse(file.HasUnrealizedChildren);
        StringAssert.Contains(file.AccessibleName, "file");
        StringAssert.Contains(file.AccessibleName, "700 B on disk");
    }

    [TestMethod]
    public async Task SelectingFileOutsideCurrentScopeNavigatesToParentAndUsesTreemapIdentity()
    {
        var vm = new StorageExplorerViewModel(new Source(Snapshot()));
        await vm.LoadScenarioAsync("test");
        vm.NavigateTreeItem(StorageTestBuilder.FileId);

        Assert.AreEqual(StorageTestBuilder.FolderId, vm.CurrentScope?.Id);
        Assert.AreEqual(StorageTestBuilder.FileId, vm.SelectedRow?.Id);
        Assert.AreSame(vm.VisibleItems.Single(), vm.SelectedRow);
        var file = vm.RevealTreeItem(StorageTestBuilder.FileId)!;
        Assert.AreEqual(vm.SelectedRow!.ColorIndex, file.ColorIndex);
        Assert.AreEqual(vm.SelectedRow.SizeDisplay, file.SizeDisplay);
        Assert.HasCount(2, vm.Breadcrumbs);
        Assert.IsFalse(vm.TreeRoots.Single().Children.Single(item => item.Id == OtherId).HasMaterialisedChildren);

        vm.NavigateUp();

        Assert.AreEqual(StorageTestBuilder.RootId, vm.CurrentScope?.Id);
        Assert.AreEqual(-1, file.ColorIndex, "off-scope rows must not imply a matching rectangle");
        Assert.AreEqual(-1, vm.TreeRoots.Single().Children.Single(item => item.Id == EmptyId).ColorIndex);
    }

    [TestMethod]
    public async Task ReorderingKeepsTreeColorsMatchedToStableTreemapRows()
    {
        var vm = new StorageExplorerViewModel(new Source(Snapshot(700), Snapshot(2000)));
        await vm.LoadScenarioAsync("test");
        var root = vm.TreeRoots.Single();
        var folder = root.Children.Single(item => item.Id == StorageTestBuilder.FolderId);
        var other = root.Children.Single(item => item.Id == OtherId);
        int originalColor = folder.ColorIndex;
        var changes = new List<NotifyCollectionChangedAction>();
        root.Children.CollectionChanged += (_, e) => changes.Add(e.Action);

        await vm.RefreshAsync();

        Assert.AreSame(folder, root.Children.Single(item => item.Id == folder.Id));
        Assert.AreEqual(originalColor, folder.ColorIndex);
        Assert.AreEqual(vm.VisibleItems.Single(row => row.Id == folder.Id).ColorIndex, folder.ColorIndex);
        Assert.AreEqual(vm.VisibleItems.Single(row => row.Id == other.Id).ColorIndex, other.ColorIndex);
        Assert.AreEqual(folder.Id, root.Children[0].Id);
        Assert.IsFalse(folder.HasMaterialisedChildren);
        CollectionAssert.DoesNotContain(changes, NotifyCollectionChangedAction.Reset);
    }

    [TestMethod]
    public async Task MaterializedFilesGrowInPlaceAndSelectionSurvivesRefresh()
    {
        var vm = new StorageExplorerViewModel(new Source(Snapshot(700), Snapshot(2000)));
        await vm.LoadScenarioAsync("test");
        vm.NavigateTreeItem(StorageTestBuilder.FileId);
        var file = vm.RevealTreeItem(StorageTestBuilder.FileId)!;
        var selected = vm.SelectedRow;

        await vm.RefreshAsync();

        Assert.AreSame(file, vm.RevealTreeItem(StorageTestBuilder.FileId));
        Assert.AreSame(selected, vm.SelectedRow);
        Assert.AreEqual(2000, file.Node.SizeBytes);
        Assert.AreEqual(vm.SelectedRow!.ColorIndex, file.ColorIndex);
        Assert.AreEqual(vm.SelectedRow.SizeDisplay, file.SizeDisplay);
    }

    [TestMethod]
    public async Task FolderOnlyPartialAcceptsFilesWithoutReplacingTheExpandedBranch()
    {
        var complete = Snapshot();
        var partial = StorageTestBuilder.Snapshot(nodes: complete.Nodes.Where(node => node.Kind == StorageNodeKind.Folder));
        var vm = new StorageExplorerViewModel(new Source(partial, complete));
        await vm.LoadScenarioAsync("test");
        vm.NavigateTreeItem(StorageTestBuilder.FolderId);
        var folder = vm.RevealTreeItem(StorageTestBuilder.FolderId)!;
        Assert.IsEmpty(folder.MaterializedChildren);

        await vm.RefreshAsync();

        Assert.AreSame(folder, vm.RevealTreeItem(folder.Id));
        Assert.IsTrue(folder.IsExpanded);
        Assert.AreEqual(StorageTestBuilder.FileId, folder.MaterializedChildren.Single().Id);
    }

    private static StorageSnapshot Snapshot(long bytes = 700) => StorageTestBuilder.Snapshot(nodes:
    [
        StorageTestBuilder.Node(StorageTestBuilder.RootId, null, "root", @"M:\", StorageNodeKind.Folder, bytes + 1000, 4),
        StorageTestBuilder.Node(StorageTestBuilder.FolderId, StorageTestBuilder.RootId, "src", @"M:\src", StorageNodeKind.Folder, bytes, 1),
        StorageTestBuilder.Node(StorageTestBuilder.FileId, StorageTestBuilder.FolderId, "app.bin", @"M:\src\app.bin", StorageNodeKind.File, bytes),
        StorageTestBuilder.Node(OtherId, StorageTestBuilder.RootId, "other", @"M:\other", StorageNodeKind.Folder, 1000, 1),
        StorageTestBuilder.Node(EmptyId, StorageTestBuilder.RootId, "empty.bin", @"M:\empty.bin", StorageNodeKind.File, 0),
    ]);

    private sealed class Source(params StorageSnapshot[] snapshots) : IStorageSnapshotSource
    {
        private int _index;

        public Task<StorageSnapshot> GetSnapshotAsync(StorageSnapshotRequest request,
            IProgress<StorageScanProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(snapshots[Math.Min(_index++, snapshots.Length - 1)]);
    }
}
