using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DevDriveStorage;

public enum ExplorerMode
{
    Folders,
    LargestFiles,
}

public enum ExplorerSortColumn
{
    Name,
    Size,
    Logical,
    ItemCount,
    Context,
    Type,
    Modified,
}

public enum ExplorerSortDirection
{
    Ascending,
    Descending,
}

public enum ExplorerScanState
{
    Idle,
    Scanning,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>
/// One row in the item table. Carries the presentation facts that depend on the
/// current scope (share of scope, colour identity) so that the table, the treemap,
/// and the inspector always agree on how an item is described.
/// </summary>
public sealed class StorageRowViewModel
{
    /// <summary>Number of distinct colour slots a host may use for rank-based colouring.</summary>
    public const int ColorSlots = 6;

    public StorageRowViewModel(StorageNode node, long scopeBytes, int sizeRank)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        ShareOfScope = scopeBytes <= 0 ? 0 : (double)node.SizeBytes / scopeBytes;
        ColorIndex = sizeRank < 0 ? 0 : sizeRank % ColorSlots;
        SizeRank = sizeRank;
    }

    public StorageNode Node { get; }

    public Guid Id => Node.Id;

    public string Name => Node.Name;

    public string PhysicalPath => Node.PhysicalPath;

    public bool IsFolder => Node.Kind == StorageNodeKind.Folder;

    public long SizeBytes => Node.SizeBytes;

    public string SizeDisplay => Node.SizeDisplay;

    public string LogicalDisplay => Node.LogicalDisplay;

    public string ItemCountDisplay => Node.ItemCountDisplay;

    public string ContextDisplay => Node.ContextDisplay;

    public double ShareOfScope { get; }

    public int SizeRank { get; }

    public string ShareDisplay => ShareOfScope >= 0.001
        ? ShareOfScope.ToString("P1")
        : ShareOfScope > 0 ? "<0.1%" : "0%";

    public int ColorIndex { get; }

    public string AutomationId => $"Row_{Node.Id:N}";

    public string AccessibleDescription =>
        $"{Name}, {SizeDisplay}, {ShareDisplay} of scope, {ContextDisplay}";
}

public sealed class StorageTreeItemViewModel : ObservableObject
{
    private readonly Func<StorageNode, IEnumerable<StorageTreeItemViewModel>> _childFactory;
    private readonly ObservableCollection<StorageTreeItemViewModel> _children = [];
    private bool _childrenMaterialized;
    private bool _isExpanded;

    public StorageTreeItemViewModel(
        StorageNode node,
        Func<StorageNode, IEnumerable<StorageTreeItemViewModel>> childFactory)
    {
        Node = node;
        _childFactory = childFactory;
    }

    public StorageNode Node { get; }

    public string Name => Node.Name;

    public string SizeDisplay => Node.SizeDisplay;

    public Guid Id => Node.Id;

    public string AutomationId => $"FolderTreeItem_{Node.Id:N}";

    public string AccessibleName => $"{Node.Name}, {Node.SizeDisplay}";

    /// <summary>
    /// Children are created on first access rather than up front. A real volume holds hundreds of
    /// thousands of folders, and building a view model for every one of them before the tree has
    /// drawn a single row costs minutes and gigabytes for rows nobody asked to see.
    /// </summary>
    public ObservableCollection<StorageTreeItemViewModel> Children
    {
        get
        {
            if (!_childrenMaterialized)
            {
                _childrenMaterialized = true;
                foreach (StorageTreeItemViewModel child in _childFactory(Node))
                {
                    _children.Add(child);
                }
            }

            return _children;
        }
    }

    /// <summary>True when children exist, answered without materialising them.</summary>
    public bool HasMaterialisedChildren => _childrenMaterialized;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}

public sealed class StorageExplorerViewModel : ObservableObject
{
    private readonly IStorageSnapshotSource _source;
    private CancellationTokenSource? _scanCancellation;
    private StorageSnapshot? _snapshot;
    private StorageNode? _currentScope;
    private StorageRowViewModel? _selectedRow;
    private string _searchText = string.Empty;
    private ExplorerMode _mode;
    private ExplorerSortColumn _sortColumn = ExplorerSortColumn.Size;
    private ExplorerSortDirection _sortDirection = ExplorerSortDirection.Descending;
    private ExplorerScanState _scanState;
    private double _progress;
    private string _progressLabel = "Ready";
    private string? _errorMessage;
    private string _scenarioId = "baseline";
    private int _refreshOrdinal;
    private bool _isEmpty = true;

    public StorageExplorerViewModel(IStorageSnapshotSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsScanning);
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => !IsScanning);
        CancelCommand = new RelayCommand(CancelScan, () => IsScanning);
        SortNameCommand = new RelayCommand(() => SortBy(ExplorerSortColumn.Name));
        SortSizeCommand = new RelayCommand(() => SortBy(ExplorerSortColumn.Size));
        SortLogicalCommand = new RelayCommand(() => SortBy(ExplorerSortColumn.Logical));
        SortItemsCommand = new RelayCommand(() => SortBy(ExplorerSortColumn.ItemCount));
        SortContextCommand = new RelayCommand(() => SortBy(ExplorerSortColumn.Context));
        SortTypeCommand = new RelayCommand(() => SortBy(ExplorerSortColumn.Type));
        SortModifiedCommand = new RelayCommand(() => SortBy(ExplorerSortColumn.Modified));
    }

    public ObservableCollection<StorageTreeItemViewModel> TreeRoots { get; } = [];

    public BulkObservableCollection<StorageRowViewModel> VisibleItems { get; } = [];

    public ObservableCollection<StorageNode> Breadcrumbs { get; } = [];

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand SortNameCommand { get; }

    public IRelayCommand SortSizeCommand { get; }

    public IRelayCommand SortLogicalCommand { get; }

    public IRelayCommand SortItemsCommand { get; }

    public IRelayCommand SortContextCommand { get; }

    public IRelayCommand SortTypeCommand { get; }

    public IRelayCommand SortModifiedCommand { get; }

    public StorageSnapshot? Snapshot
    {
        get => _snapshot;
        private set => SetProperty(ref _snapshot, value);
    }

    public StorageNode? CurrentScope
    {
        get => _currentScope;
        private set
        {
            if (SetProperty(ref _currentScope, value))
            {
                OnPropertyChanged(nameof(ScopeSummary));
                OnPropertyChanged(nameof(ScopeName));
                OnPropertyChanged(nameof(CanNavigateUp));
            }
        }
    }

    /// <summary>
    /// The selected row. Settable so a list control can drive selection directly;
    /// <see cref="Select(Guid)"/> is the equivalent entry point for callers that
    /// only hold an identifier.
    /// </summary>
    public StorageRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                OnPropertyChanged(nameof(SelectedNode));
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public StorageNode? SelectedNode => SelectedRow?.Node;

    public bool HasSelection => SelectedRow is not null;

    public bool CanNavigateUp => CurrentScope?.ParentId is not null;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                RebuildProjection();
            }
        }
    }

    public ExplorerMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value))
            {
                return;
            }

            SortColumn = ExplorerSortColumn.Size;
            SortDirection = ExplorerSortDirection.Descending;
            NotifySortGlyphs();
            OnPropertyChanged(nameof(ModeCaption));
            RebuildProjection();
        }
    }

    public string ModeCaption => Mode == ExplorerMode.Folders
        ? "Immediate contents of this folder"
        : "Every file below this folder, largest first";

    public ExplorerSortColumn SortColumn
    {
        get => _sortColumn;
        private set => SetProperty(ref _sortColumn, value);
    }

    public ExplorerSortDirection SortDirection
    {
        get => _sortDirection;
        private set => SetProperty(ref _sortDirection, value);
    }

    public ExplorerScanState ScanState
    {
        get => _scanState;
        private set
        {
            if (SetProperty(ref _scanState, value))
            {
                OnPropertyChanged(nameof(IsScanning));
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(IsEmptyStateVisible));
                RefreshCommand.NotifyCanExecuteChanged();
                RetryCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsScanning => ScanState == ExplorerScanState.Scanning;

    public bool HasError => ScanState == ExplorerScanState.Failed;

    public bool IsEmpty
    {
        get => _isEmpty;
        private set
        {
            if (SetProperty(ref _isEmpty, value))
            {
                OnPropertyChanged(nameof(IsEmptyStateVisible));
            }
        }
    }

    public bool IsEmptyStateVisible => IsEmpty && !IsScanning;

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string ProgressLabel
    {
        get => _progressLabel;
        private set => SetProperty(ref _progressLabel, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string ScopeName => CurrentScope?.Name ?? "No scope";

    public string ScopeSummary => CurrentScope is null
        ? "Nothing scanned yet"
        : $"{CurrentScope.SizeDisplay} · {CurrentScope.ItemCount:N0} items";

    public string ScopeLogicalSummary => CurrentScope?.LogicalBytes is long logical
        ? $"{ByteSizeFormatter.Format(logical)} logical"
        : "Logical size not reported";

    public string ItemCountSummary =>
        VisibleItems.Count == 1 ? "1 item" : $"{VisibleItems.Count:N0} items";

    public string CoverageSummary => Snapshot?.Coverage.Display ?? "No coverage";

    /// <summary>
    /// Honest coverage detail for the status bar. Unexpected denials degrade the scan and are
    /// reported as Partial; OS-owned exclusions (System Volume Information and friends) are stated
    /// as excluded on an otherwise Complete scan, because they are denied on every machine and a
    /// warning that never turns off is a warning nobody reads. Empty only when neither applies.
    /// </summary>
    public string CoverageDetail
    {
        get
        {
            if (Snapshot is not { } snapshot)
            {
                return string.Empty;
            }

            int denied = snapshot.Coverage.DeniedPaths.Length;
            int excluded = snapshot.Coverage.ExcludedPaths.Length;

            if (snapshot.Completion == SnapshotCompletion.Partial)
            {
                return denied switch
                {
                    0 => "Partial coverage",
                    1 => "Partial · 1 path denied",
                    _ => $"Partial · {denied:N0} paths denied",
                };
            }

            return excluded switch
            {
                0 => string.Empty,
                1 => "Complete · 1 system folder excluded",
                _ => $"Complete · {excluded:N0} system folders excluded",
            };
        }
    }

    public bool HasCoverageDetail => CoverageDetail.Length > 0;

    public string SnapshotSummary => Snapshot is null
        ? "No snapshot"
        : $"Scanned {Snapshot.CapturedAtUtc.ToLocalTime():MMM d, HH:mm}";

    public string SortGlyphName => GlyphFor(ExplorerSortColumn.Name);

    public string SortGlyphSize => GlyphFor(ExplorerSortColumn.Size);

    public string SortGlyphLogical => GlyphFor(ExplorerSortColumn.Logical);

    public string SortGlyphItems => GlyphFor(ExplorerSortColumn.ItemCount);

    public string SortGlyphContext => GlyphFor(ExplorerSortColumn.Context);

    /// <summary>
    /// What the current snapshot is of — for the live source, the root path being scanned. Exposed
    /// so a page rebuilt on navigation can restore which scope its chrome is pointing at; the view
    /// model itself survives, but the page has no other way to learn what it is showing.
    /// </summary>
    public string ScenarioId => _scenarioId;

    public async Task LoadScenarioAsync(string scenarioId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        _scenarioId = scenarioId;
        OnPropertyChanged(nameof(ScenarioId));
        _refreshOrdinal = 0;

        // A scenario switch is a clean slate: never carry scope or selection across
        // two unrelated mock drives.
        CurrentScope = null;
        SelectedRow = null;
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        _mode = ExplorerMode.Folders;
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(ModeCaption));
        SortColumn = ExplorerSortColumn.Size;
        SortDirection = ExplorerSortDirection.Descending;
        NotifySortGlyphs();
        await LoadAsync();
    }

    public async Task RefreshAsync()
    {
        _refreshOrdinal++;
        await LoadAsync();
    }

    public Task RetryAsync() => LoadAsync();

    public void CancelScan() => _scanCancellation?.Cancel();

    public void NavigateTo(Guid id)
    {
        StorageNode? node = Snapshot?.Find(id);
        if (node is null)
        {
            return;
        }

        if (node.Kind == StorageNodeKind.File)
        {
            Select(id);
            return;
        }

        CurrentScope = node;
        RebuildBreadcrumbs();
        RebuildProjection();
        SelectedRow = CreateRow(node, node.SizeBytes, -1);
        ExpandTreePath(id);
    }

    public void NavigateUp()
    {
        if (CurrentScope?.ParentId is Guid parentId)
        {
            NavigateTo(parentId);
        }
    }

    public void Select(Guid id)
    {
        StorageRowViewModel? row = VisibleItems.FirstOrDefault(item => item.Id == id);
        if (row is not null)
        {
            SelectedRow = row;
            return;
        }

        if (Snapshot?.Find(id) is StorageNode node)
        {
            SelectedRow = CreateRow(node, CurrentScope?.SizeBytes ?? node.SizeBytes, -1);
        }
    }

    public void SortBy(ExplorerSortColumn column)
    {
        if (SortColumn == column)
        {
            SortDirection = SortDirection == ExplorerSortDirection.Ascending
                ? ExplorerSortDirection.Descending
                : ExplorerSortDirection.Ascending;
        }
        else
        {
            SortColumn = column;
            SortDirection = column is ExplorerSortColumn.Name or ExplorerSortColumn.Context
                ? ExplorerSortDirection.Ascending
                : ExplorerSortDirection.Descending;
        }

        NotifySortGlyphs();
        RebuildProjection();
    }

    private async Task LoadAsync()
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        var scan = new CancellationTokenSource();
        _scanCancellation = scan;
        CancellationToken token = scan.Token;
        ErrorMessage = null;
        Progress = 0;
        ProgressLabel = "Starting scan";
        ScanState = ExplorerScanState.Scanning;

        // A source is free to scan on any thread: the live scanner walks the tree on a thread-pool
        // thread, so its progress reports arrive off the thread that started the scan. Raising
        // property change notifications from there would drive UI bindings off the UI thread, so
        // reports are marshalled back onto the context that started the scan. When the report
        // already arrives on the starting thread — synchronous sources and unit tests — it is
        // applied inline so report ordering stays deterministic.
        SynchronizationContext? origin = SynchronizationContext.Current;

        void ApplyProgress(StorageScanProgress update)
        {
            // A marshalled report can land after its scan already finished or was superseded by a
            // newer one. Applying it then would overwrite the terminal label with a stale value.
            if (!ReferenceEquals(_scanCancellation, scan) || ScanState != ExplorerScanState.Scanning)
            {
                return;
            }

            Progress = update.Fraction;
            ProgressLabel = update.Label;

            if (update.Partial is { } partial)
            {
                AdoptSnapshot(partial);
            }
        }

        Action<StorageScanProgress> applyProgress = ApplyProgress;
        var progress = new InlineProgress<StorageScanProgress>(update =>
        {
            if (origin is null || ReferenceEquals(origin, SynchronizationContext.Current))
            {
                applyProgress(update);
                return;
            }

            origin.Post(
                static state =>
                {
                    (Action<StorageScanProgress> apply, StorageScanProgress value) =
                        ((Action<StorageScanProgress>, StorageScanProgress))state!;
                    apply(value);
                },
                (applyProgress, update));
        });

        Guid? previousScopeId = CurrentScope?.Id;
        try
        {
            StorageSnapshot snapshot = await _source.GetSnapshotAsync(
                new StorageSnapshotRequest(_scenarioId, previousScopeId, _refreshOrdinal),
                progress,
                token);
            AdoptSnapshot(snapshot);

            Progress = 1;
            ProgressLabel = snapshot.Completion == SnapshotCompletion.Partial
                ? "Scan complete with partial coverage"
                : "Scan complete";
            ScanState = ExplorerScanState.Completed;
        }
        catch (OperationCanceledException)
        {
            ProgressLabel = "Scan cancelled";
            ScanState = ExplorerScanState.Cancelled;
        }
        catch (Exception exception) when (
            exception is StorageSnapshotSourceException or StorageSnapshotValidationException)
        {
            ErrorMessage = exception.Message;
            ProgressLabel = "Scan failed";
            ScanState = ExplorerScanState.Failed;
        }
    }

    /// <summary>
    /// Swaps in a new snapshot without moving the user. Called for each streamed partial as well as
    /// for the final result, so it has to put the room back exactly where it was: same scope, same
    /// selected row, same expanded folders. That only works because the live source derives node
    /// ids from paths — with freshly minted ids nothing here would match and every update would
    /// throw the user back to the root.
    /// </summary>
    private void AdoptSnapshot(StorageSnapshot snapshot)
    {
        Guid? previousScopeId = CurrentScope?.Id;
        Guid? previousSelectionId = SelectedRow?.Id;
        HashSet<Guid> expanded = CollectExpandedIds();

        Snapshot = snapshot;
        BuildTree();

        CurrentScope = previousScopeId is Guid scopeId &&
            snapshot.Find(scopeId) is { Kind: StorageNodeKind.Folder } scope
                ? scope
                : snapshot.Root;

        RebuildBreadcrumbs();
        RebuildProjection();

        SelectedRow = previousSelectionId is Guid selectionId &&
            VisibleItems.FirstOrDefault(row => row.Id == selectionId) is { } restored
                ? restored
                : CreateRow(CurrentScope, CurrentScope.SizeBytes, -1);

        expanded.UnionWith(snapshot.AncestorsAndSelf(CurrentScope.Id).Select(node => node.Id));
        foreach (StorageTreeItemViewModel root in TreeRoots)
        {
            Expand(root, expanded);
        }

        OnPropertyChanged(nameof(CoverageSummary));
        OnPropertyChanged(nameof(CoverageDetail));
        OnPropertyChanged(nameof(HasCoverageDetail));
        OnPropertyChanged(nameof(SnapshotSummary));
        OnPropertyChanged(nameof(ScopeLogicalSummary));
    }

    /// <summary>
    /// The folders the user has opened in the tree rail. Only walks branches that have already been
    /// materialised — an unexpanded branch cannot contain an expanded one, and touching it would
    /// force the whole tree into memory to answer a question about a handful of rows.
    /// </summary>
    private HashSet<Guid> CollectExpandedIds()
    {
        var expanded = new HashSet<Guid>();
        var pending = new Stack<StorageTreeItemViewModel>(TreeRoots);
        while (pending.Count > 0)
        {
            StorageTreeItemViewModel item = pending.Pop();
            if (!item.IsExpanded)
            {
                continue;
            }

            expanded.Add(item.Id);
            if (!item.HasMaterialisedChildren)
            {
                continue;
            }

            foreach (StorageTreeItemViewModel child in item.Children)
            {
                pending.Push(child);
            }
        }

        return expanded;
    }

    private void BuildTree()
    {
        TreeRoots.Clear();
        if (Snapshot is null)
        {
            return;
        }

        TreeRoots.Add(BuildTreeItem(Snapshot, Snapshot.Root));
    }

    private StorageTreeItemViewModel BuildTreeItem(StorageSnapshot snapshot, StorageNode node) =>
        new(node, parent => snapshot.ChildrenOf(parent.Id)
            .Where(child => child.Kind == StorageNodeKind.Folder)
            .OrderByDescending(child => child.SizeBytes)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Select(child => BuildTreeItem(snapshot, child)));

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        if (Snapshot is null || CurrentScope is null)
        {
            return;
        }

        foreach (StorageNode node in Snapshot.AncestorsAndSelf(CurrentScope.Id))
        {
            Breadcrumbs.Add(node);
        }
    }

    private void RebuildProjection()
    {
        if (Snapshot is null || CurrentScope is null)
        {
            VisibleItems.ReplaceAll([]);
            IsEmpty = true;
            OnPropertyChanged(nameof(ItemCountSummary));
            return;
        }

        IEnumerable<StorageNode> query = Mode == ExplorerMode.Folders
            ? Snapshot.ChildrenOf(CurrentScope.Id)
            : Snapshot.DescendantsOf(CurrentScope.Id)
                .Where(node => node.Kind == StorageNodeKind.File);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(node =>
                node.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                node.PhysicalPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        StorageNode[] matches = query.ToArray();

        // Colour identity follows size rank, not the active sort, so re-sorting the
        // table never reshuffles the colours that tie rows to treemap rectangles.
        Dictionary<Guid, int> sizeRanks = matches
            .OrderByDescending(node => node.SizeBytes)
            .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .Select((node, rank) => (node.Id, rank))
            .ToDictionary(pair => pair.Id, pair => pair.rank);

        long scopeBytes = CurrentScope.SizeBytes;
        VisibleItems.ReplaceAll(ApplySort(matches)
            .Select(node => CreateRow(node, scopeBytes, sizeRanks[node.Id])));

        IsEmpty = VisibleItems.Count == 0;
        OnPropertyChanged(nameof(ItemCountSummary));
    }

    private static StorageRowViewModel CreateRow(StorageNode node, long scopeBytes, int sizeRank) =>
        new(node, scopeBytes, sizeRank);

    private IEnumerable<StorageNode> ApplySort(IEnumerable<StorageNode> query)
    {
        IOrderedEnumerable<StorageNode> ordered = SortColumn switch
        {
            ExplorerSortColumn.Size => Direct(node => node.SizeBytes),
            ExplorerSortColumn.Logical => Direct(node => node.LogicalBytes ?? node.SizeBytes),
            ExplorerSortColumn.ItemCount => Direct(node => node.ItemCount),
            ExplorerSortColumn.Type => Direct(node => (int)node.Kind),
            ExplorerSortColumn.Modified => Direct(node => node.ModifiedAtUtc),
            ExplorerSortColumn.Context => SortDirection == ExplorerSortDirection.Ascending
                ? query.OrderBy(node => node.ContextDisplay, StringComparer.OrdinalIgnoreCase)
                : query.OrderByDescending(node => node.ContextDisplay, StringComparer.OrdinalIgnoreCase),
            _ => SortDirection == ExplorerSortDirection.Ascending
                ? query.OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                : query.OrderByDescending(node => node.Name, StringComparer.OrdinalIgnoreCase),
        };

        return ordered.ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase);

        IOrderedEnumerable<StorageNode> Direct<TKey>(Func<StorageNode, TKey> key) =>
            SortDirection == ExplorerSortDirection.Ascending
                ? query.OrderBy(key)
                : query.OrderByDescending(key);
    }

    private string GlyphFor(ExplorerSortColumn column)
    {
        if (SortColumn != column)
        {
            return string.Empty;
        }

        // Segoe Fluent Icons: chevron up / chevron down.
        return SortDirection == ExplorerSortDirection.Ascending ? "\uE70E" : "\uE70D";
    }

    private void NotifySortGlyphs()
    {
        OnPropertyChanged(nameof(SortGlyphName));
        OnPropertyChanged(nameof(SortGlyphSize));
        OnPropertyChanged(nameof(SortGlyphLogical));
        OnPropertyChanged(nameof(SortGlyphItems));
        OnPropertyChanged(nameof(SortGlyphContext));
    }

    private void ExpandTreePath(Guid id)
    {
        if (Snapshot is null)
        {
            return;
        }

        HashSet<Guid> path = Snapshot.AncestorsAndSelf(id).Select(node => node.Id).ToHashSet();
        foreach (StorageTreeItemViewModel root in TreeRoots)
        {
            Expand(root, path);
        }
    }

    /// <summary>
    /// Expands only along the path to the target. Walking every branch would collapse rows the user
    /// left open elsewhere and, worse, materialise the entire folder tree to do it.
    /// </summary>
    private static void Expand(StorageTreeItemViewModel item, HashSet<Guid> path)
    {
        if (!path.Contains(item.Id))
        {
            return;
        }

        item.IsExpanded = true;
        foreach (StorageTreeItemViewModel child in item.Children)
        {
            Expand(child, path);
        }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
