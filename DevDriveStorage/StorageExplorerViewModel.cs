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
    private bool _isExpanded;

    public StorageTreeItemViewModel(
        StorageNode node,
        IEnumerable<StorageTreeItemViewModel> children)
    {
        Node = node;
        Children = new ObservableCollection<StorageTreeItemViewModel>(children);
    }

    public StorageNode Node { get; }

    public string Name => Node.Name;

    public string SizeDisplay => Node.SizeDisplay;

    public Guid Id => Node.Id;

    public string AutomationId => $"FolderTreeItem_{Node.Id:N}";

    public string AccessibleName => $"{Node.Name}, {Node.SizeDisplay}";

    public ObservableCollection<StorageTreeItemViewModel> Children { get; }

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
        ? "No mock snapshot loaded"
        : $"{CurrentScope.SizeDisplay} allocated · {CurrentScope.ItemCount:N0} items";

    public string ScopeLogicalSummary => CurrentScope?.LogicalBytes is long logical
        ? $"{ByteSizeFormatter.Format(logical)} logical"
        : "Logical size not reported";

    public string ItemCountSummary =>
        VisibleItems.Count == 1 ? "1 item" : $"{VisibleItems.Count:N0} items";

    public string CoverageSummary => Snapshot?.Coverage.Display ?? "No coverage";

    public string SnapshotSummary => Snapshot is null
        ? "No snapshot"
        : $"Mock snapshot · {Snapshot.CapturedAtUtc:MMM d, HH:mm} UTC";

    public string SortGlyphName => GlyphFor(ExplorerSortColumn.Name);

    public string SortGlyphSize => GlyphFor(ExplorerSortColumn.Size);

    public string SortGlyphLogical => GlyphFor(ExplorerSortColumn.Logical);

    public string SortGlyphItems => GlyphFor(ExplorerSortColumn.ItemCount);

    public string SortGlyphContext => GlyphFor(ExplorerSortColumn.Context);

    public async Task LoadScenarioAsync(string scenarioId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
        _scenarioId = scenarioId;
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
        _scanCancellation = new CancellationTokenSource();
        CancellationToken token = _scanCancellation.Token;
        ErrorMessage = null;
        Progress = 0;
        ProgressLabel = "Starting mock scan";
        ScanState = ExplorerScanState.Scanning;

        var progress = new InlineProgress<StorageScanProgress>(update =>
        {
            Progress = update.Fraction;
            ProgressLabel = update.Label;
        });

        Guid? previousScopeId = CurrentScope?.Id;
        Guid? previousSelectionId = SelectedRow?.Id;
        try
        {
            StorageSnapshot snapshot = await _source.GetSnapshotAsync(
                new StorageSnapshotRequest(_scenarioId, previousScopeId, _refreshOrdinal),
                progress,
                token);
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

            ExpandTreePath(CurrentScope.Id);
            Progress = 1;
            ProgressLabel = snapshot.Completion == SnapshotCompletion.Partial
                ? "Mock scan complete with partial coverage"
                : "Mock scan complete";
            ScanState = ExplorerScanState.Completed;
            OnPropertyChanged(nameof(CoverageSummary));
            OnPropertyChanged(nameof(SnapshotSummary));
            OnPropertyChanged(nameof(ScopeLogicalSummary));
        }
        catch (OperationCanceledException)
        {
            ProgressLabel = "Mock scan cancelled";
            ScanState = ExplorerScanState.Cancelled;
        }
        catch (Exception exception) when (
            exception is StorageSnapshotSourceException or StorageSnapshotValidationException)
        {
            ErrorMessage = exception.Message;
            ProgressLabel = "Mock scan failed";
            ScanState = ExplorerScanState.Failed;
        }
    }

    private void BuildTree()
    {
        TreeRoots.Clear();
        if (Snapshot is null)
        {
            return;
        }

        TreeRoots.Add(BuildTreeItem(Snapshot.Root));
    }

    private StorageTreeItemViewModel BuildTreeItem(StorageNode node)
    {
        IEnumerable<StorageTreeItemViewModel> children = Snapshot!.ChildrenOf(node.Id)
            .Where(child => child.Kind == StorageNodeKind.Folder)
            .OrderByDescending(child => child.SizeBytes)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Select(BuildTreeItem);
        return new StorageTreeItemViewModel(node, children);
    }

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

    private static void Expand(StorageTreeItemViewModel item, HashSet<Guid> path)
    {
        item.IsExpanded = path.Contains(item.Id);
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
