using DevDriveStorage;

namespace DevDriveStorage.Tests;

[TestClass]
public sealed class StorageExplorerViewModelTests
{
    [TestMethod]
    public async Task LoadBuildsTreeBreadcrumbAndFolderProjection()
    {
        var viewModel = new StorageExplorerViewModel(new SequenceSource(StorageTestBuilder.Snapshot()));

        await viewModel.LoadScenarioAsync("test");

        Assert.AreEqual("Mock drive (M:)", viewModel.CurrentScope?.Name);
        Assert.HasCount(1, viewModel.TreeRoots);
        Assert.HasCount(1, viewModel.Breadcrumbs);
        Assert.AreEqual("src", viewModel.VisibleItems.Single().Name);
        Assert.AreEqual(ExplorerScanState.Completed, viewModel.ScanState);
    }

    [TestMethod]
    public async Task NavigationSearchSortAndSelectionStaySynchronized()
    {
        Guid file2Id = Guid.Parse("10000000-0000-0000-0000-000000000004");
        var snapshot = StorageTestBuilder.Snapshot(nodes:
        [
            StorageTestBuilder.Node(StorageTestBuilder.RootId, null, "root", @"M:\",
                StorageNodeKind.Folder, 1000, 3),
            StorageTestBuilder.Node(StorageTestBuilder.FolderId, StorageTestBuilder.RootId, "src",
                @"M:\src", StorageNodeKind.Folder, 1000, 2),
            StorageTestBuilder.Node(StorageTestBuilder.FileId, StorageTestBuilder.FolderId, "small.log",
                @"M:\src\small.log", StorageNodeKind.File, 100),
            StorageTestBuilder.Node(file2Id, StorageTestBuilder.FolderId, "LARGE.BIN",
                @"M:\src\LARGE.BIN", StorageNodeKind.File, 900),
        ]);
        var viewModel = new StorageExplorerViewModel(new SequenceSource(snapshot));
        await viewModel.LoadScenarioAsync("test");

        viewModel.NavigateTo(StorageTestBuilder.FolderId);
        viewModel.Mode = ExplorerMode.LargestFiles;
        viewModel.SearchText = "bin";
        viewModel.SortBy(ExplorerSortColumn.Size);
        viewModel.Select(file2Id);

        Assert.HasCount(2, viewModel.Breadcrumbs);
        Assert.AreEqual("LARGE.BIN", viewModel.VisibleItems.Single().Name);
        Assert.AreEqual(file2Id, viewModel.SelectedNode?.Id);
        Assert.AreEqual(StorageTestBuilder.FolderId, viewModel.CurrentScope?.Id);
    }

    [TestMethod]
    public async Task ProgressCancellationFailureAndRetryAreExplicit()
    {
        var source = new ControllableSource(StorageTestBuilder.Snapshot());
        var viewModel = new StorageExplorerViewModel(source);
        Task load = viewModel.LoadScenarioAsync("scanning");
        await source.Started.Task;
        Assert.AreEqual(ExplorerScanState.Scanning, viewModel.ScanState);
        Assert.AreEqual(0.4, viewModel.Progress, 0.001);

        viewModel.CancelScan();
        await load;
        Assert.AreEqual(ExplorerScanState.Cancelled, viewModel.ScanState);

        source.ShouldFail = true;
        await viewModel.LoadScenarioAsync("failure");
        Assert.AreEqual(ExplorerScanState.Failed, viewModel.ScanState);
        StringAssert.Contains(viewModel.ErrorMessage, "scripted failure");

        source.ShouldFail = false;
        source.Release();
        await viewModel.RetryAsync();
        Assert.AreEqual(ExplorerScanState.Completed, viewModel.ScanState);
        Assert.IsNull(viewModel.ErrorMessage);
    }

    [TestMethod]
    public async Task RefreshReconcilesRemovedSelectionToCurrentScope()
    {
        var original = StorageTestBuilder.Snapshot();
        var refreshed = StorageTestBuilder.Snapshot("changed", nodes:
        [
            StorageTestBuilder.Node(StorageTestBuilder.RootId, null, "root", @"M:\",
                StorageNodeKind.Folder, 300, 1),
            StorageTestBuilder.Node(StorageTestBuilder.FolderId, StorageTestBuilder.RootId, "src",
                @"M:\src", StorageNodeKind.Folder, 300),
        ]);
        var viewModel = new StorageExplorerViewModel(new SequenceSource(original, refreshed));
        await viewModel.LoadScenarioAsync("changed-refresh");
        viewModel.NavigateTo(StorageTestBuilder.FolderId);
        viewModel.Select(StorageTestBuilder.FileId);

        await viewModel.RefreshAsync();

        Assert.AreEqual(StorageTestBuilder.FolderId, viewModel.CurrentScope?.Id);
        Assert.AreEqual(StorageTestBuilder.FolderId, viewModel.SelectedNode?.Id);
    }

    [TestMethod]
    public async Task LargeProjectionRemainsDeterministic()
    {
        var catalog = MockStorageScenarioCatalog.CreateDefault();
        var viewModel = new StorageExplorerViewModel(new MockStorageSnapshotSource(catalog, TimeSpan.Zero));

        await viewModel.LoadScenarioAsync("large");
        viewModel.Mode = ExplorerMode.LargestFiles;

        Assert.IsGreaterThanOrEqualTo(2000, viewModel.VisibleItems.Count);
        Assert.IsTrue(viewModel.VisibleItems.Zip(viewModel.VisibleItems.Skip(1))
            .All(pair => pair.First.SizeBytes >= pair.Second.SizeBytes));
    }

    [TestMethod]
    public async Task RowsReportShareOfScopeAndLogicalSize()
    {
        var catalog = MockStorageScenarioCatalog.CreateDefault();
        var viewModel = new StorageExplorerViewModel(
            new MockStorageSnapshotSource(catalog, TimeSpan.Zero));

        await viewModel.LoadScenarioAsync("baseline");

        StorageRowViewModel projects = viewModel.VisibleItems
            .Single(row => row.Name == "Projects");
        StorageRowViewModel machines = viewModel.VisibleItems
            .Single(row => row.Name == "Virtual machines");

        // 286 GB of a 712 GB scope.
        Assert.AreEqual(0.4017, projects.ShareOfScope, 0.001);
        Assert.AreEqual(1.0, viewModel.VisibleItems.Sum(row => row.ShareOfScope), 0.001);

        // Sparse virtual disks allocate far less than they appear to occupy, and the
        // difference is the point of showing both numbers.
        Assert.AreEqual("1.30 TB", machines.LogicalDisplay);
        Assert.AreEqual("214 GB", machines.SizeDisplay);
        Assert.IsTrue(machines.Node.HasLogicalDifference);
        Assert.AreEqual("—", projects.LogicalDisplay);
    }

    [TestMethod]
    public async Task ColourIdentityFollowsSizeRankAndSurvivesResorting()
    {
        var catalog = MockStorageScenarioCatalog.CreateDefault();
        var viewModel = new StorageExplorerViewModel(
            new MockStorageSnapshotSource(catalog, TimeSpan.Zero));
        await viewModel.LoadScenarioAsync("baseline");

        Dictionary<string, int> bySize = viewModel.VisibleItems
            .ToDictionary(row => row.Name, row => row.ColorIndex);
        Assert.AreEqual(0, bySize["Projects"], "The largest item owns the first colour.");

        viewModel.SortBy(ExplorerSortColumn.Name);

        Assert.AreNotEqual(
            "Projects",
            viewModel.VisibleItems[0].Name,
            "Sorting by name should reorder the table.");
        foreach (StorageRowViewModel row in viewModel.VisibleItems)
        {
            Assert.AreEqual(
                bySize[row.Name],
                row.ColorIndex,
                $"'{row.Name}' changed colour when the table was re-sorted.");
        }
    }

    [TestMethod]
    public async Task SwitchingScenarioResetsScopeSelectionAndSearch()
    {
        var catalog = MockStorageScenarioCatalog.CreateDefault();
        var viewModel = new StorageExplorerViewModel(
            new MockStorageSnapshotSource(catalog, TimeSpan.Zero));
        await viewModel.LoadScenarioAsync("baseline");
        viewModel.NavigateTo(Guid.Parse("22222222-2222-2222-2222-222222222221"));
        viewModel.SearchText = "zip";

        // 'stale-provider' reuses the baseline node IDs, so a stale scope would
        // silently survive the switch if the reset were missing.
        await viewModel.LoadScenarioAsync("stale-provider");

        Assert.AreEqual(viewModel.Snapshot?.RootId, viewModel.CurrentScope?.Id);
        Assert.AreEqual(string.Empty, viewModel.SearchText);
        Assert.AreEqual(ExplorerMode.Folders, viewModel.Mode);
        Assert.HasCount(1, viewModel.Breadcrumbs);
        StringAssert.Contains(viewModel.ScopeSummary, "712 GB");
    }

    [TestMethod]
    public async Task NavigateUpMovesToTheParentScope()
    {
        var catalog = MockStorageScenarioCatalog.CreateDefault();
        var viewModel = new StorageExplorerViewModel(
            new MockStorageSnapshotSource(catalog, TimeSpan.Zero));
        await viewModel.LoadScenarioAsync("baseline");
        Assert.IsFalse(viewModel.CanNavigateUp, "The root has no parent.");

        viewModel.NavigateTo(Guid.Parse("22222222-2222-2222-2222-222222222221"));
        Assert.IsTrue(viewModel.CanNavigateUp);

        viewModel.NavigateUp();

        Assert.AreEqual(viewModel.Snapshot?.RootId, viewModel.CurrentScope?.Id);
        Assert.IsFalse(viewModel.CanNavigateUp);
    }

    private sealed class SequenceSource(params StorageSnapshot[] snapshots) : IStorageSnapshotSource
    {
        private int _index;

        public Task<StorageSnapshot> GetSnapshotAsync(
            StorageSnapshotRequest request,
            IProgress<StorageScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new StorageScanProgress(1, "Complete", 1, 1));
            return Task.FromResult(snapshots[Math.Min(_index++, snapshots.Length - 1)]);
        }
    }

    private sealed class ControllableSource(StorageSnapshot snapshot) : IStorageSnapshotSource
    {
        private TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ShouldFail { get; set; }

        public void Release() => _release.TrySetResult();

        public async Task<StorageSnapshot> GetSnapshotAsync(
            StorageSnapshotRequest request,
            IProgress<StorageScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (ShouldFail)
            {
                throw new StorageSnapshotSourceException("scripted failure");
            }

            progress?.Report(new StorageScanProgress(0.4, "Indexing mock files", 400, 1000));
            Started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return snapshot;
        }
    }
}
