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

    /// <summary>
    /// The live scanner walks the tree on a thread-pool thread, so its progress reports arrive off
    /// the thread that started the scan. Applying them there would raise property change
    /// notifications off the UI thread and drive bindings illegally, so they must be routed through
    /// the synchronization context that started the scan.
    /// </summary>
    [TestMethod]
    public async Task OffThreadProgressIsMarshalledThroughTheStartingContext()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        var context = new CountingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var viewModel = new StorageExplorerViewModel(
                new OffThreadProgressSource(StorageTestBuilder.Snapshot()));

            await viewModel.RefreshAsync();

            Assert.IsGreaterThan(
                0,
                context.ProgressPosts,
                "an off-thread progress report must be posted to the starting context");
            Assert.AreEqual(ExplorerScanState.Completed, viewModel.ScanState);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>
    /// A source that reports on the calling thread must still be applied inline, so synchronous
    /// sources and tests keep deterministic report ordering rather than racing a posted callback.
    /// </summary>
    [TestMethod]
    public async Task SameThreadProgressIsAppliedWithoutPosting()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        var context = new CountingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var viewModel = new StorageExplorerViewModel(
                new SequenceSource(StorageTestBuilder.Snapshot()));

            await viewModel.RefreshAsync();

            Assert.AreEqual(
                0,
                context.ProgressPosts,
                "a same-thread report must be applied inline, not posted");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [TestMethod]
    public async Task StreamedPartialFillsTheRoomBeforeTheScanFinishes()
    {
        StorageSnapshot partial = StorageTestBuilder.Snapshot(
            nodes:
            [
                StorageTestBuilder.Node(StorageTestBuilder.RootId, null, "root", @"M:\",
                    StorageNodeKind.Folder, 1000, 1),
                StorageTestBuilder.Node(StorageTestBuilder.FolderId, StorageTestBuilder.RootId, "src",
                    @"M:\src", StorageNodeKind.Folder, 1000, 0),
            ]);

        var viewModel = new StorageExplorerViewModel(
            new StreamingSource(partial, StorageTestBuilder.Snapshot()));
        var sawRowsWhileScanning = false;

        viewModel.PropertyChanged += (_, args) =>
        {
            // The whole point of streaming: rows exist while the state is still Scanning.
            if (args.PropertyName == nameof(viewModel.Progress) &&
                viewModel.ScanState == ExplorerScanState.Scanning &&
                viewModel.VisibleItems.Count > 0)
            {
                sawRowsWhileScanning = true;
            }
        };

        await viewModel.LoadScenarioAsync("test");

        Assert.IsTrue(sawRowsWhileScanning, "a partial snapshot must populate the table mid-scan");
        Assert.AreEqual(ExplorerScanState.Completed, viewModel.ScanState);
        Assert.AreEqual("src", viewModel.VisibleItems.Single().Name, "the final scan still wins");
    }

    [TestMethod]
    public async Task StreamedPartialKeepsScopeAndSelectionPut()
    {
        StorageSnapshot snapshot = StorageTestBuilder.Snapshot();
        var viewModel = new StorageExplorerViewModel(new StreamingSource(snapshot, snapshot));

        await viewModel.LoadScenarioAsync("test");
        viewModel.NavigateTo(StorageTestBuilder.FolderId);
        Guid scopeId = viewModel.CurrentScope!.Id;

        // A second scan streams a partial before finishing. Losing the user's place on every
        // update is the failure mode that makes live results worse than a spinner.
        await viewModel.RefreshAsync();

        Assert.AreEqual(scopeId, viewModel.CurrentScope?.Id, "a streamed update must not reset the scope");
    }

    [TestMethod]
    public async Task AScanThatWasSupersededDoesNotStampItsEndingOnItsReplacement()
    {
        // Refresh cancels the scan in flight and immediately starts another. The cancelled scan's
        // continuation then resumes — after the replacement has already set itself Scanning — and
        // used to write "Scan cancelled" straight over it. The label is the small half of the bug:
        // ApplyProgress drops every partial once ScanState is no longer Scanning, so the live scan
        // went silent and the room sat on a half-drawn tree until it finally finished.
        //
        // The gates matter. If the first scan parks on its own cancellation token, Cancel() runs
        // its continuation inline on the calling thread — before the replacement has assigned
        // itself — and the overlap this test exists to create never happens. Parking on something
        // the token does not touch is what lets the two scans genuinely interleave, the way a UI
        // SynchronizationContext makes them interleave in the app.
        var source = new GatedSource(StorageTestBuilder.Snapshot());
        var viewModel = new StorageExplorerViewModel(source);

        Task first = viewModel.LoadScenarioAsync("scanning");
        await source.Entered(0);

        Task second = viewModel.RefreshAsync();
        await source.Entered(1);

        Assert.AreEqual(
            ExplorerScanState.Scanning,
            viewModel.ScanState,
            "the replacement has taken over by now");

        source.Release(0);
        await first;

        Assert.AreEqual(
            ExplorerScanState.Scanning,
            viewModel.ScanState,
            "the superseded scan just ended, but the replacement is still running — the room " +
            "must not be told the scan was cancelled");

        source.Release(1);
        await second;

        Assert.AreEqual(ExplorerScanState.Completed, viewModel.ScanState);
    }

    [TestMethod]
    public async Task AnUnexpectedFailureEndsTheScanInsteadOfSpinningForever()
    {
        // Only three exception types were handled. A scan walks denied directories, reparse loops
        // and ReFS quirks for minutes, so something else getting out is a when, not an if — and
        // when it did, ScanState stayed Scanning: ring spinning, Refresh and Retry disabled, and
        // Cancel doing nothing. Restarting the app was the only way back.
        var viewModel = new StorageExplorerViewModel(
            new ThrowingSource(new InvalidOperationException("something nobody predicted")));

        await viewModel.LoadScenarioAsync("test");

        Assert.AreEqual(ExplorerScanState.Failed, viewModel.ScanState);
        Assert.IsFalse(viewModel.IsScanning, "a dead scan must not keep the room disabled");
        StringAssert.Contains(viewModel.ErrorMessage, "something nobody predicted");
    }

    private sealed class ThrowingSource(Exception exception) : IStorageSnapshotSource
    {
        public Task<StorageSnapshot> GetSnapshotAsync(
            StorageSnapshotRequest request,
            IProgress<StorageScanProgress>? progress,
            CancellationToken cancellationToken) => throw exception;
    }

    /// <summary>
    /// Parks each scan on a gate the caller opens by hand, so two scans can be held open at once
    /// and released in a chosen order. The gate is deliberately independent of the cancellation
    /// token: cancellation is observed only after the gate opens, which is what puts a superseded
    /// scan's ending after its replacement's beginning.
    /// </summary>
    private sealed class GatedSource(StorageSnapshot snapshot) : IStorageSnapshotSource
    {
        private readonly List<(TaskCompletionSource Entered, TaskCompletionSource Release)> _gates = [];

        public Task Entered(int index) => Gate(index).Entered.Task;

        public void Release(int index) => Gate(index).Release.TrySetResult();

        private (TaskCompletionSource Entered, TaskCompletionSource Release) Gate(int index)
        {
            lock (_gates)
            {
                while (_gates.Count <= index)
                {
                    _gates.Add((
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)));
                }

                return _gates[index];
            }
        }

        public async Task<StorageSnapshot> GetSnapshotAsync(
            StorageSnapshotRequest request,
            IProgress<StorageScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            int index;
            lock (_gates)
            {
                index = _gates.Count(g => g.Entered.Task.IsCompleted);
            }

            (TaskCompletionSource entered, TaskCompletionSource release) = Gate(index);
            entered.TrySetResult();
            await release.Task;

            cancellationToken.ThrowIfCancellationRequested();
            return snapshot;
        }
    }

    /// <summary>Reports one partial snapshot, then completes with the final one.</summary>
    private sealed class StreamingSource(StorageSnapshot partial, StorageSnapshot final)
        : IStorageSnapshotSource
    {
        public Task<StorageSnapshot> GetSnapshotAsync(
            StorageSnapshotRequest request,
            IProgress<StorageScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new StorageScanProgress(0.5, "Scanning", 1, 2, partial));
            return Task.FromResult(final);
        }
    }

    /// <summary>
    /// Counts posts that carry a progress payload. Everything is forwarded to the thread pool so
    /// awaited continuations still run and the test cannot deadlock on an unpumped queue.
    /// </summary>
    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        private int _progressPosts;

        public int ProgressPosts => Volatile.Read(ref _progressPosts);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (state is ValueTuple<Action<StorageScanProgress>, StorageScanProgress>)
            {
                Interlocked.Increment(ref _progressPosts);
            }

            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    private sealed class OffThreadProgressSource(StorageSnapshot snapshot) : IStorageSnapshotSource
    {
        public async Task<StorageSnapshot> GetSnapshotAsync(
            StorageSnapshotRequest request,
            IProgress<StorageScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            await Task.Run(
                () => progress?.Report(new StorageScanProgress(0.5, "Halfway", 1, 2)),
                cancellationToken);

            return snapshot;
        }
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
