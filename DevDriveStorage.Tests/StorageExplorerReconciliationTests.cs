using System.Collections.Specialized;
using DevDriveStorage;

namespace DevDriveStorage.Tests;

/// <summary>
/// A streaming scan republishes the same tree many times a second. These tests pin the property
/// that makes that bearable: the view models are edited in place, so the controls bound to them
/// keep their containers, their expansion and the user's selection instead of being rebuilt.
/// </summary>
[TestClass]
public sealed class StorageExplorerReconciliationTests
{
    private static readonly Guid DocsId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid BuildId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid NestedId = Guid.Parse("20000000-0000-0000-0000-000000000003");
    private static readonly Guid LateId = Guid.Parse("20000000-0000-0000-0000-000000000004");

    [TestMethod]
    public async Task AStreamedUpdateKeepsTheRowObjectsAndGrowsThemInPlace()
    {
        var viewModel = new StorageExplorerViewModel(new ScriptedSource(
            [Stage(docs: 100, build: 40)],
            [Stage(docs: 500, build: 40), Stage(docs: 900, build: 40)]));

        await viewModel.LoadScenarioAsync("test");
        StorageRowViewModel docs = viewModel.VisibleItems.Single(row => row.Id == DocsId);
        Assert.AreEqual(100, docs.SizeBytes);

        await viewModel.RefreshAsync();

        Assert.AreSame(
            docs,
            viewModel.VisibleItems.Single(row => row.Id == DocsId),
            "the row objects must survive a partial, containers and all");
        Assert.AreEqual(900, docs.SizeBytes, "and must carry the newest numbers");
    }

    [TestMethod]
    public async Task AStreamedUpdateNeverResetsTheTable()
    {
        var viewModel = new StorageExplorerViewModel(new ScriptedSource(
            [Stage(docs: 100, build: 40)],
            [Stage(docs: 500, build: 40), Stage(docs: 900, build: 40), Stage(docs: 900, build: 40)]));

        var resets = 0;
        ((INotifyCollectionChanged)viewModel.VisibleItems).CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                resets++;
            }
        };

        await viewModel.LoadScenarioAsync("test");
        await viewModel.RefreshAsync();

        // A reset tells the list that everything it has built is gone, which is exactly the
        // rebuild-per-tick this design exists to stop.
        Assert.AreEqual(0, resets, "a streamed update must not reset the table");
    }

    [TestMethod]
    public async Task AnExpandedFolderStaysExpandedAndKeepsItsTreeItem()
    {
        var viewModel = new StorageExplorerViewModel(new ScriptedSource(
            [Stage(docs: 100, build: 40)],
            [Stage(docs: 500, build: 40), Stage(docs: 900, build: 40)]));
        await viewModel.LoadScenarioAsync("test");

        StorageTreeItemViewModel root = viewModel.TreeRoots.Single();
        StorageTreeItemViewModel docs = root.Children.Single(item => item.Id == DocsId);
        docs.IsExpanded = true;
        StorageTreeItemViewModel nested = docs.Children.Single();

        await viewModel.RefreshAsync();

        Assert.AreSame(root, viewModel.TreeRoots.Single(), "the root item must be reused");
        Assert.AreSame(docs, root.Children.Single(item => item.Id == DocsId));
        Assert.AreSame(nested, docs.Children.Single(), "an opened branch keeps its own items too");
        Assert.IsTrue(docs.IsExpanded, "the folders the user opened must stay open");
        Assert.AreEqual(900, docs.Node.SizeBytes, "the reused item must show the newest size");
    }

    [TestMethod]
    public async Task AnUnopenedBranchIsNotMaterialisedToUpdateIt()
    {
        var viewModel = new StorageExplorerViewModel(new ScriptedSource(
            [Stage(docs: 100, build: 40)],
            [Stage(docs: 900, build: 40)]));
        await viewModel.LoadScenarioAsync("test");

        StorageTreeItemViewModel docs = viewModel.TreeRoots.Single()
            .Children.Single(item => item.Id == DocsId);
        Assert.IsFalse(docs.HasMaterialisedChildren);

        await viewModel.RefreshAsync();

        // Building view models for rows nobody has opened, on every tick, is exactly the cost the
        // lazy Children accessor exists to avoid.
        Assert.IsFalse(
            docs.HasMaterialisedChildren,
            "reconciliation must not force an unopened branch into memory");
    }

    [TestMethod]
    public async Task AFolderThatOvertakesItsSiblingIsRepositionedRatherThanRebuilt()
    {
        // build starts smaller than docs and ends larger, which is what a real scan does to
        // siblings ordered by size while it is still counting.
        var viewModel = new StorageExplorerViewModel(new ScriptedSource(
            [Stage(docs: 900, build: 100)],
            [Stage(docs: 900, build: 5_000)]));
        await viewModel.LoadScenarioAsync("test");

        StorageTreeItemViewModel root = viewModel.TreeRoots.Single();
        StorageTreeItemViewModel build = root.Children.Single(item => item.Id == BuildId);
        StorageRowViewModel buildRow = viewModel.VisibleItems.Single(row => row.Id == BuildId);
        Assert.AreEqual(BuildId, root.Children[1].Id, "build starts behind docs");
        Assert.AreEqual(BuildId, viewModel.VisibleItems[1].Id);

        await viewModel.RefreshAsync();

        Assert.AreEqual(BuildId, root.Children[0].Id, "the bigger folder must rise");
        Assert.AreEqual(BuildId, viewModel.VisibleItems[0].Id, "and the table must agree");
        Assert.AreSame(build, root.Children[0], "re-ranking repositions an item, it does not replace it");
        Assert.AreSame(buildRow, viewModel.VisibleItems[0]);
    }

    [TestMethod]
    public async Task AnOpenedFolderStaysOpenWhenItOvertakesItsSibling()
    {
        // docs has to be the one that rises. An item that falls is never removed — it drifts as
        // others are inserted ahead of it — so only the riser's container is torn down, and only the
        // riser exercises the restore.
        var viewModel = new StorageExplorerViewModel(new ScriptedSource(
            [Stage(docs: 100, build: 900)],
            [Stage(docs: 5_000, build: 900)]));
        await viewModel.LoadScenarioAsync("test");

        StorageTreeItemViewModel root = viewModel.TreeRoots.Single();
        StorageTreeItemViewModel docs = root.Children.Single(item => item.Id == DocsId);
        Assert.AreEqual(DocsId, root.Children[1].Id, "docs must start behind build");
        docs.IsExpanded = true;

        // TreeView ignores a move, so a re-ranked child has to be removed and re-inserted — and the
        // TreeViewItem being torn down collapses, which the two-way IsExpanded binding writes back
        // to the view model. There is no TreeView here, so stand in for it: nothing else in the
        // library ever assigns false, and without this the restore could be deleted and the test
        // would still pass.
        ((INotifyCollectionChanged)root.Children).CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Remove)
            {
                foreach (StorageTreeItemViewModel removed in args.OldItems!.Cast<StorageTreeItemViewModel>())
                {
                    removed.IsExpanded = false;
                }
            }
        };

        await viewModel.RefreshAsync();

        Assert.AreEqual(DocsId, root.Children[0].Id, "docs must have overtaken build");
        Assert.IsTrue(docs.IsExpanded, "and must still be open");
    }

    [TestMethod]
    public async Task ArrivalsAndDeparturesAreAppliedWithoutTouchingTheRest()
    {
        var viewModel = new StorageExplorerViewModel(new ScriptedSource(
            [Stage(docs: 900, build: 100)],
            [Stage(docs: 900, build: 100, includeLate: true)],
            [Stage(docs: 900, build: 100, includeLate: true, includeDocs: false)]));
        await viewModel.LoadScenarioAsync("test");

        StorageRowViewModel build = viewModel.VisibleItems.Single(row => row.Id == BuildId);

        await viewModel.RefreshAsync();
        Assert.IsTrue(
            viewModel.VisibleItems.Any(row => row.Id == LateId),
            "a folder found mid-scan has to appear");

        await viewModel.RefreshAsync();
        Assert.IsFalse(
            viewModel.VisibleItems.Any(row => row.Id == DocsId),
            "a folder the newest snapshot no longer has must drop out");
        Assert.AreSame(
            build,
            viewModel.VisibleItems.Single(row => row.Id == BuildId),
            "and its neighbours must be left alone");
    }

    [TestMethod]
    public async Task TheScopeRowIsAdoptedInsteadOfBeingMintedEachTick()
    {
        var viewModel = new StorageExplorerViewModel(new ScriptedSource(
            [Stage(docs: 100, build: 40)],
            [Stage(docs: 900, build: 400)]));
        await viewModel.LoadScenarioAsync("test");

        // Selecting the scope itself produces a synthetic row that is not in the table, so nothing
        // in the projection can restore it. Replacing it every tick would rebind the inspector ten
        // times a second for a selection that never changed.
        StorageRowViewModel? scopeRow = viewModel.SelectedRow;
        Assert.IsNotNull(scopeRow);
        Assert.AreEqual(viewModel.CurrentScope?.Id, scopeRow.Id);

        await viewModel.RefreshAsync();

        Assert.AreSame(scopeRow, viewModel.SelectedRow, "the scope row must be adopted, not replaced");
        Assert.AreEqual(1_300, viewModel.SelectedRow!.SizeBytes, "and must carry the newest total");
    }

    [TestMethod]
    public async Task ARowRaisesNothingWhenNothingAboutItMoved()
    {
        var viewModel = new StorageExplorerViewModel(
            new ScriptedSource([Stage(docs: 900, build: 100)]));
        await viewModel.LoadScenarioAsync("test");

        StorageRowViewModel row = viewModel.VisibleItems.Single(item => item.Id == DocsId);
        var raised = 0;
        row.PropertyChanged += (_, _) => raised++;

        // A scan builds a fresh StorageNode for every node on every partial, so this is what a tick
        // that found nothing new actually hands the row — a different instance saying the same thing.
        // Passing back the instance it already holds would test the one input production never sends.
        row.Adopt(row.Node with { }, 1_000, row.SizeRank);

        Assert.AreEqual(0, raised, "an unchanged row must not wake the bindings that read it");
    }

    [TestMethod]
    public async Task ARowRefusesToAdoptADifferentItem()
    {
        var viewModel = new StorageExplorerViewModel(
            new ScriptedSource([Stage(docs: 900, build: 100)]));
        await viewModel.LoadScenarioAsync("test");

        StorageRowViewModel docs = viewModel.VisibleItems.Single(row => row.Id == DocsId);
        StorageRowViewModel build = viewModel.VisibleItems.Single(row => row.Id == BuildId);

        // Silently re-pointing a row at an unrelated node would desynchronise it from the container
        // the list has built for it, and would surface as wrong data rather than as a crash.
        Assert.ThrowsExactly<ArgumentException>(() => docs.Adopt(build.Node, 1_000, 0));
    }

    [TestMethod]
    public async Task AScenarioSwitchStillStartsFromAnEmptyTree()
    {
        var viewModel = new StorageExplorerViewModel(
            new ScriptedSource([Stage(docs: 900, build: 100)]));
        await viewModel.LoadScenarioAsync("first");
        StorageTreeItemViewModel first = viewModel.TreeRoots.Single();

        await viewModel.LoadScenarioAsync("second");

        // Two scenarios that happen to share a root id are still two different drives.
        Assert.AreNotSame(first, viewModel.TreeRoots.Single());
    }

    [TestMethod]
    public async Task TheProjectionRevisionAdvancesEvenWhenNoRowMoves()
    {
        var viewModel = new StorageExplorerViewModel(new ScriptedSource(
            [Stage(docs: 900, build: 100)],
            [Stage(docs: 950, build: 100)]));
        await viewModel.LoadScenarioAsync("test");
        int before = viewModel.ProjectionRevision;

        await viewModel.RefreshAsync();

        // Only the numbers changed, so the collection raised nothing. A view that redraws itself
        // from the projection — the treemap — would otherwise never hear about it.
        Assert.IsGreaterThan(before, viewModel.ProjectionRevision);
    }

    private static StorageSnapshot Stage(
        long docs,
        long build,
        bool includeLate = false,
        bool includeDocs = true)
    {
        List<StorageNode> nodes =
        [
            StorageTestBuilder.Node(
                StorageTestBuilder.RootId, null, "Mock drive (M:)", @"M:\",
                StorageNodeKind.Folder,
                (includeDocs ? docs : 0) + build + (includeLate ? 10 : 0),
                4),
            StorageTestBuilder.Node(
                BuildId, StorageTestBuilder.RootId, "build", @"M:\build",
                StorageNodeKind.Folder, build, 1),
        ];

        if (includeDocs)
        {
            nodes.Add(StorageTestBuilder.Node(
                DocsId, StorageTestBuilder.RootId, "docs", @"M:\docs",
                StorageNodeKind.Folder, docs, 1));
            nodes.Add(StorageTestBuilder.Node(
                NestedId, DocsId, "images", @"M:\docs\images",
                StorageNodeKind.Folder, docs, 0));
        }

        if (includeLate)
        {
            nodes.Add(StorageTestBuilder.Node(
                LateId, StorageTestBuilder.RootId, "late", @"M:\late",
                StorageNodeKind.Folder, 10, 0));
        }

        return StorageTestBuilder.Snapshot(nodes: nodes);
    }

    /// <summary>
    /// One entry per scan. Every stage but the last is reported as a streamed partial; the last is
    /// the result the scan completes with. The final entry is reused once the script runs out.
    /// </summary>
    private sealed class ScriptedSource(params StorageSnapshot[][] scans) : IStorageSnapshotSource
    {
        private int _scan;

        public Task<StorageSnapshot> GetSnapshotAsync(
            StorageSnapshotRequest request,
            IProgress<StorageScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            StorageSnapshot[] stages = scans[Math.Min(_scan++, scans.Length - 1)];
            for (int i = 0; i < stages.Length - 1; i++)
            {
                progress?.Report(new StorageScanProgress(
                    (i + 1) / (double)(stages.Length + 1),
                    "Scanning",
                    0,
                    0,
                    stages[i]));
            }

            return Task.FromResult(stages[^1]);
        }
    }
}
