using System.Collections.ObjectModel;
using System.ComponentModel;
using DevDriveManager.Controls;
using DevDriveStorage;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace DevDriveManager.Pages;

/// <summary>One label/value pair in the Space inspector.</summary>
/// <remarks>
/// A sealed class rather than a record: XamlTypeInfo generates setters for a positional record
/// declared in the same project and used as an <c>x:DataType</c>, which fails against init-only
/// properties with CS8852.
/// </remarks>
public sealed class InspectorFact(string label, string value)
{
    public string Label { get; } = label;

    public string Value { get; } = value;
}

/// <summary>
/// The Space room: what is actually on the volume, by size.
/// </summary>
/// <remarks>
/// This is the storage prototype folded into the manager. <see cref="StorageExplorerViewModel"/>
/// and the treemap geometry already lived in <c>DevDriveStorage</c>, so the fold moved chrome only —
/// the interaction model is the one the prototype's UI suite already proved.
/// <para>
/// Two things are deliberately different from the prototype. The scenario picker is gone: this room
/// scans real volumes, and the mock catalogue was a prototype affordance. And the volume strip is
/// the scope selector, so choosing what to scan uses the same control that reports the result.
/// </para>
/// <para>
/// The room never scans on load. A cold full-volume scan is minutes of I/O, so starting one because
/// the user happened to click a rail button would be hostile. The scan is always an explicit act.
/// </para>
/// </remarks>
public sealed partial class SpacePage : Page, INotifyPropertyChanged
{
    /// <summary>
    /// Page-level properties that are computed from the ViewModel rather than stored. They are
    /// raised together because every one of them is a projection of the same scan state, so
    /// tracking which subset changed would cost more than it saves.
    /// </summary>
    private static readonly string[] DerivedProperties =
    [
        nameof(ShowOverlay),
        nameof(IsScanning),
        nameof(LiveScanText),
        nameof(OverlayTitle),
        nameof(OverlayDetail),
        nameof(CoverageBlock),
        nameof(InspectorTitle),
        nameof(InspectorPath),
        nameof(NameColumnWidth),
    ];

    private readonly IVolumeProvider _volumes;
    private StorageRowViewModel? _watchedRow;
    private bool _isLoaded;
    private string? _pendingVolumeLabel;
    /// <summary>
    /// The volume the strip currently has selected. This is scope, not a scan: picking a chip
    /// changes what the button will do, and nothing else. Before this room shared the scan bar,
    /// clicking a chip started a full volume scan on the spot, which made a click that is free in
    /// every other room cost minutes of disk I/O in this one.
    /// </summary>
    private StorageVolume? _selectedVolume;

    private double _nameColumnWidth = 220;

    public SpacePage()
        : this(new SystemVolumeProvider())
    {
    }

    public SpacePage(IVolumeProvider volumes)
    {
        _volumes = volumes;

        // App-lifetime, not page-owned: see App.SharedSpace. Live-only by design — the prototype
        // routed through RoutingStorageSnapshotSource so its scenario picker could reach the mock
        // catalogue; a shipping room that scans real volumes has no business carrying a mock. The
        // scenario id is simply the root path to scan.
        ViewModel = App.SharedSpace;
        StripEntries = BuildStrip();
        InitializeComponent();

        // Subscribed on Loaded and released on Unloaded because the ViewModel outlives this page.
        // Subscribing in the constructor would leave one live handler per visit to the room, each
        // holding a page the user has already navigated away from.
        Loaded += OnLoadedSubscribe;
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            WatchSelectedRow(null);
            DetachItemsSources();
        };
    }

    private void OnLoadedSubscribe(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        WatchSelectedRow(ViewModel.SelectedRow);
        AttachItemsSources();
    }

    /// <summary>
    /// Drops every list this page pointed at a collection on the shared ViewModel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page already releases its own handlers on <c>Unloaded</c>, for the reason stated in the
    /// constructor. These are the subscriptions XAML makes on its behalf: an <c>ItemsSource</c>
    /// binding to a collection on <see cref="App.SharedSpace"/> registers a <em>native</em>
    /// listener that outlives the page, so with <see cref="Microsoft.UI.Xaml.Controls.Page.NavigationCacheMode"/>
    /// left at its Disabled default — a fresh page per visit — every trip to the room left another
    /// torn-down control listening to a collection that is still very much alive.
    /// </para>
    /// <para>
    /// Two crash dumps caught a collection change being dispatched across that boundary and
    /// fail-fasting the process, which nothing on the managed side can catch: not a
    /// <c>try</c>/<c>catch</c>, not <see cref="Application.UnhandledException"/>. One held seven
    /// live <c>SpacePage</c> instances and a <c>CollectionChanged</c> chain seven handlers deep.
    /// </para>
    /// <para>
    /// <see cref="StorageExplorerViewModel.TreeRoots"/> and
    /// <see cref="StorageExplorerViewModel.VisibleItems"/> matter more here than the breadcrumb
    /// they were found alongside: a streaming scan reconciles both many times a second, where the
    /// trail only moves when someone navigates.
    /// </para>
    /// </remarks>
    private void DetachItemsSources()
    {
        FolderTree.ItemsSource = null;
        ItemsList.ItemsSource = null;
        ScopeBreadcrumbBar.ItemsSource = null;
    }

    /// <summary>
    /// Puts back what <see cref="DetachItemsSources"/> dropped.
    /// </summary>
    /// <remarks>
    /// Belt and braces rather than the thing that makes navigation work: NavigationCacheMode is
    /// Disabled, so WinUI builds a new page on every visit and <c>x:Bind</c> assigns each
    /// <c>ItemsSource</c> in the constructor anyway — verified by removing this and watching the
    /// room come back populated regardless. It earns its place for the case that assignment does
    /// not cover, a page WinUI reloads without reconstructing, where without it the lists would
    /// return empty and stay that way. Safe to repeat because every binding here is <c>x:Bind</c>'s
    /// implicit OneTime, so nothing re-pushes a source behind it.
    /// </remarks>
    private void AttachItemsSources()
    {
        FolderTree.ItemsSource = ViewModel.TreeRoots;
        ItemsList.ItemsSource = ViewModel.VisibleItems;
        ScopeBreadcrumbBar.ItemsSource = ViewModel.Breadcrumbs;
    }

    /// <summary>
    /// Follows the selected row itself, not just the act of selecting one.
    /// </summary>
    /// <remarks>
    /// A streaming scan keeps the same row object and grows its numbers, so the selection never
    /// changes while the values behind the inspector do. Watching only
    /// <see cref="StorageExplorerViewModel.SelectedRow"/> would leave the inspector reading out the
    /// size the folder had when it was clicked, for the rest of the scan.
    /// </remarks>
    private void WatchSelectedRow(StorageRowViewModel? row)
    {
        if (ReferenceEquals(_watchedRow, row))
        {
            return;
        }

        if (_watchedRow is not null)
        {
            _watchedRow.PropertyChanged -= SelectedRow_PropertyChanged;
        }

        _watchedRow = row;
        if (_watchedRow is not null)
        {
            _watchedRow.PropertyChanged += SelectedRow_PropertyChanged;
        }
    }

    private void SelectedRow_PropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        UpdateInspector();

    /// <summary>
    /// Records a row the user picked, and ignores the list emptying its own selection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SelectedItem</c> used to be a TwoWay <c>x:Bind</c>. A <c>ListView</c> drops its selection
    /// when its items go away, so the release in <see cref="DetachItemsSources"/> wrote that null
    /// back into <see cref="App.SharedSpace"/> and the inspector came back describing nothing.
    /// Proved in the Reclaim room, which carries the identical pair and where the loss is countable:
    /// 30 rows before leaving, 0 on return, against 30 on the build without the release.
    /// </para>
    /// <para>
    /// Reading one way and writing only on a real pick is immune to <em>when</em> the list clears
    /// itself; capturing and restoring the selection around the detach was tried first and did not
    /// hold. The ViewModel can still clear its own selection — the OneWay read carries that to the
    /// list — but the list cannot clear the ViewModel's.
    /// </para>
    /// </remarks>
    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems.Count > 0 && args.AddedItems[0] is StorageRowViewModel row)
        {
            ViewModel.SelectedRow = row;
        }
    }

    public StorageExplorerViewModel ViewModel { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Volumes offered as scan scopes.</summary>
    public IReadOnlyList<VolumeStripEntry> StripEntries { get; }

    public ObservableCollection<InspectorFact> InspectorFacts { get; } = [];

    /// <summary>
    /// True whenever the centre column has nothing worth showing — before the first scan, while a
    /// scan is still working with nothing to show yet, and after a failure. Once the scan streams
    /// its first partial the overlay gets out of the way: watching the tree fill in is the point of
    /// streaming, and it cannot be seen through a full-bleed "Scanning" card.
    /// </summary>
    public bool ShowOverlay =>
        ViewModel.ScanState is ExplorerScanState.Idle
            or ExplorerScanState.Failed
            or ExplorerScanState.Cancelled ||
        (ViewModel.ScanState is ExplorerScanState.Scanning && ViewModel.Snapshot is null);

    /// <summary>
    /// True while a scan is running, whether or not rows have started arriving. This drives the
    /// scan controls on the volume strip, which is the single place in the room that owns scan
    /// state — there is no second progress bar or cancel button anywhere below it.
    /// </summary>
    public bool IsScanning => ViewModel.ScanState is ExplorerScanState.Scanning;

    /// <summary>Reminds the reader that live rows are still moving, and offers the way out.</summary>
    public string LiveScanText => _pendingVolumeLabel is null
        ? "Scanning — results update as they are found"
        : $"Scanning {_pendingVolumeLabel} — results update as they are found";

    public string OverlayTitle => ViewModel.ScanState switch
    {
        ExplorerScanState.Scanning => _pendingVolumeLabel is null
            ? "Scanning"
            : $"Scanning {_pendingVolumeLabel}",
        ExplorerScanState.Failed => "That scan did not finish",
        ExplorerScanState.Cancelled => "Scan cancelled",
        _ => "Pick a volume to scan",
    };

    public string OverlayDetail => ViewModel.ScanState switch
    {
        ExplorerScanState.Scanning => ViewModel.ProgressLabel,
        ExplorerScanState.Failed => ViewModel.ErrorMessage ?? "No detail was reported.",
        ExplorerScanState.Cancelled => "Nothing was changed. Pick a volume above to start again.",

        // The honest warning belongs here, before the wait, not in a toast afterwards.
        _ => "Choose one of the volumes above. A first scan reads every directory on the "
            + "volume, so a large drive can take several minutes — you can cancel at any point.",
    };

    public string CoverageBlock
    {
        get
        {
            if (ViewModel.Snapshot is not StorageSnapshot snapshot)
            {
                return "No scan yet.";
            }

            string denied = snapshot.Coverage.DeniedPaths.Length switch
            {
                0 => "0 denied",
                1 => "1 denied",
                int n => $"{n:N0} denied",
            };

            return $"{snapshot.Root.ItemCount:N0} nodes · {denied}\n"
                + $"{ViewModel.CoverageSummary} · {ViewModel.SnapshotSummary}";
        }
    }

    public string InspectorTitle => ViewModel.SelectedRow?.Name ?? "Nothing selected";

    public string InspectorPath =>
        ViewModel.SelectedRow?.PhysicalPath ?? "Select a row or a treemap rectangle.";

    /// <summary>
    /// An explicit width for the name column, shared by the header and every row.
    /// <para>
    /// A star column is the obvious choice and does work, but it hides how tight the budget is: the
    /// four numeric columns plus their gaps and the row padding claim a fixed 276 DIPs, so on a
    /// narrow window the name silently collapses to whatever is left — it was down to 67 DIPs at
    /// 1180 DIPs of window, which reads as a rendering bug rather than a layout that ran out of
    /// room. Computing it makes the floor explicit and keeps the header aligned with the rows for
    /// free, because both read this one number.
    /// </para>
    /// </summary>
    public GridLength NameColumnWidth => new(_nameColumnWidth);

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// A live scan cannot report a fraction until it knows the tree's shape, so zero progress means
    /// "working, extent unknown" rather than "0% done". Showing a stalled bar at zero reads as hung.
    /// </summary>
    public static bool IsIndeterminate(double progress) => progress <= 0;

    /// <summary>Resolves the rank palette slot a row shares with its treemap rectangle.</summary>
    public static Brush PaletteBrush(int colorIndex) => TreemapPalette.Resolve(colorIndex);

    private IReadOnlyList<VolumeStripEntry> BuildStrip()
    {
        try
        {
            // No reclaim totals in this room, so every volume reports zero reclaimable. The strip
            // renders capacity either way; the Reclaim room is what fills that number in.
            return VolumeStrip.Build(
                _volumes.GetFixedVolumes(),
                Path.GetPathRoot(Environment.SystemDirectory));
        }
        catch (Exception)
        {
            // Volume discovery is best effort. A probe failure must leave an empty strip rather
            // than take the room down with it.
            return [];
        }
    }

    private void Page_Loaded(object sender, RoutedEventArgs args)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        RestoreScopeSelection();
        UpdateDerived();
    }

    /// <summary>
    /// Re-points the volume strip at whatever the shared view model is already showing, falling back
    /// to a sensible default scope on a first visit. The scan survives navigation; this page does
    /// not, so without the first half a completed G:\ scan would come back with its results intact
    /// but no volume card selected and a generic "Scanning" label.
    /// </summary>
    private void RestoreScopeSelection()
    {
        string scope = ViewModel.ScenarioId;
        VolumeStripEntry? entry = StripEntries.FirstOrDefault(
            candidate => string.Equals(
                candidate.Volume.RootPath,
                scope,
                StringComparison.OrdinalIgnoreCase));

        // Nothing loaded yet, so there is no scope to restore — pick one. Arriving with the room's
        // one verb greyed out states that the room cannot be used, which is not what is meant.
        entry ??= VolumeStrip.DefaultScope(StripEntries);

        if (entry is null)
        {
            return;
        }

        _pendingVolumeLabel = entry.Volume.DisplayName;
        _selectedVolume = entry.Volume;

        // The bar owns chip selection now, so restoring scope is a matter of telling it which
        // volume is current rather than walking containers that may not be realised yet.
        ScanBar.SelectedVolumePath = entry.Volume.RootPath;
        RefreshScanBar();
    }

    /// <summary>
    /// Everything in a table row that is not the name, in DIPs: the four numeric columns
    /// (84 + 70 + 74 + 48), the four 12px gaps between the five columns, the row's own 14px
    /// horizontal padding, and the card's 1px border on each side. The numeric widths are sized to
    /// their widest realistic content — the column header for SIZE ON DISK and SHARE, a
    /// seven-figure count for ITEMS — because every DIP spent here is taken from the name, which is
    /// the column people actually read.
    /// </summary>
    private const double TableFixedColumnsWidth = 84 + 70 + 74 + 48 + (12 * 4) + 28 + 2;

    /// <summary>
    /// Measures the table card, not the header grid. Measuring the header would feed back on
    /// itself: a wider name column grows the header's desired width, the grid is arranged at that
    /// desired width, and the next measurement reports the inflated number. The card's width comes
    /// from the page's star column and does not depend on anything inside it.
    /// </summary>
    private void TableCard_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        // NewSize is in DIPs, which is the same unit the ColumnDefinition widths are in. Do not
        // sanity-check this against UI-automation rectangles — those are physical pixels, and the
        // two only agree at 100% scale.
        double available = Math.Max(140, args.NewSize.Width - TableFixedColumnsWidth);
        if (Math.Abs(available - _nameColumnWidth) < 0.5)
        {
            return;
        }

        _nameColumnWidth = available;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NameColumnWidth)));
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(StorageExplorerViewModel.SelectedRow):
                WatchSelectedRow(ViewModel.SelectedRow);
                UpdateDerived();
                break;

            case nameof(StorageExplorerViewModel.ScanState):
            case nameof(StorageExplorerViewModel.Progress):
            case nameof(StorageExplorerViewModel.ProgressLabel):
            case nameof(StorageExplorerViewModel.Snapshot):
            case nameof(StorageExplorerViewModel.CurrentScope):
                UpdateDerived();
                break;
        }
    }

    /// <summary>
    /// Rebuilds the inspector only when one of the facts it prints has actually moved. A scan can
    /// raise several notifications a second on the selected row, and most of them are for values
    /// this panel does not show.
    /// </summary>
    private void UpdateInspector()
    {
        List<InspectorFact> facts = BuildInspectorFacts();
        if (facts.Count == InspectorFacts.Count &&
            facts.Zip(InspectorFacts).All(pair =>
                pair.First.Label == pair.Second.Label && pair.First.Value == pair.Second.Value))
        {
            return;
        }

        InspectorFacts.Clear();
        foreach (InspectorFact fact in facts)
        {
            InspectorFacts.Add(fact);
        }
    }

    private List<InspectorFact> BuildInspectorFacts()
    {
        if (ViewModel.SelectedRow is not StorageRowViewModel row)
        {
            return [];
        }

        List<InspectorFact> facts =
        [
            new InspectorFact("SIZE ON DISK", row.SizeDisplay),
            new InspectorFact("APPARENT SIZE", row.LogicalDisplay),
            new InspectorFact(
                "SHARE OF THIS SCOPE",
                $"{row.ShareDisplay} of {ViewModel.CurrentScope?.Name ?? "scope"}"),
            new InspectorFact(
                row.IsFolder ? "ITEMS INSIDE" : "KIND",
                row.IsFolder ? row.ItemCountDisplay : "File"),
        ];

        if (row.ContextDisplay.Length > 0)
        {
            facts.Add(new InspectorFact("WHAT PUT IT THERE", row.ContextDisplay));
        }

        return facts;
    }

    private void UpdateDerived()
    {
        foreach (string name in DerivedProperties)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        UpdateInspector();
        UpdateStatusBar();
        RefreshScanBar();
    }

    private void UpdateStatusBar()
    {
        if (SpaceStatusBar is null)
        {
            return;
        }

        SpaceStatusBar.Facts.Clear();

        if (ViewModel.Snapshot is null)
        {
            SpaceStatusBar.Facts.Add(new StatusFact(
                ViewModel.IsScanning ? ViewModel.ProgressLabel : "Not scanned yet"));
            return;
        }

        // Coverage leads, and a partial scan is emphasised rather than stated flatly: a total that
        // silently excludes denied paths is the one number a storage tool must not undersell.
        bool partial = ViewModel.Snapshot.Completion == SnapshotCompletion.Partial;
        SpaceStatusBar.Facts.Add(new StatusFact(
            ViewModel.CoverageSummary,
            partial ? StatusEmphasis.Warn : StatusEmphasis.Good));

        if (ViewModel.HasCoverageDetail)
        {
            SpaceStatusBar.Facts.Add(new StatusFact(ViewModel.CoverageDetail));
        }

        SpaceStatusBar.Facts.Add(new StatusFact(ViewModel.ScopeSummary));
        SpaceStatusBar.Facts.Add(new StatusFact(ViewModel.ItemCountSummary));
        SpaceStatusBar.Facts.Add(new StatusFact(ViewModel.SnapshotSummary));
    }

    /// <summary>
    /// Picking a volume sets the scope and nothing else. The scan bar's button is the only thing in
    /// this room, as in every other room, that spends disk I/O.
    /// </summary>
    private void ScanBar_VolumeSelected(object? sender, StorageVolume volume)
    {
        _selectedVolume = volume;
        RefreshScanBar();
    }

    private void ViewModeSelector_SelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.Mode = sender.SelectedItem == LargestFilesModeItem
            ? ExplorerMode.LargestFiles
            : ExplorerMode.Folders;

    private void FolderTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (args.AddedItems.FirstOrDefault() is StorageTreeItemViewModel item)
        {
            ViewModel.NavigateTo(item.Id);
        }
    }

    private void ScopeBreadcrumbBar_ItemClicked(
        BreadcrumbBar sender,
        BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is StorageNode node)
        {
            ViewModel.NavigateTo(node.Id);
        }
    }

    private void ItemsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (ItemsList.SelectedItem is StorageRowViewModel { IsFolder: true } row)
        {
            ViewModel.NavigateTo(row.Id);
        }
    }

    private void StorageTreemap_NodeInvoked(object sender, StorageNodeInvokedEventArgs args)
    {
        ViewModel.Select(args.Node.Id);
        if (ViewModel.SelectedRow is StorageRowViewModel row)
        {
            ItemsList.ScrollIntoView(row);
        }
    }

    private void CancelScanButton_Click(object sender, RoutedEventArgs args) =>
        ViewModel.CancelScan();

    private void RetryScanButton_Click(object sender, RoutedEventArgs args) =>
        _ = ViewModel.RetryAsync();

    private void ScanBar_CancelRequested(object? sender, EventArgs args) => ViewModel.CancelScan();

    /// <summary>
    /// Scans the selected volume. Re-running a scope that is already loaded and scanning it for the
    /// first time are the same request from the user's side, so they are the same button — the only
    /// difference is whether the view model can reuse what it already has.
    /// </summary>
    private void ScanBar_ScanRequested(object? sender, EventArgs args)
    {
        if (_selectedVolume is not StorageVolume volume)
        {
            return;
        }

        _pendingVolumeLabel = volume.DisplayName;

        _ = ViewModel.Snapshot is not null &&
            string.Equals(ViewModel.ScenarioId, volume.RootPath, StringComparison.OrdinalIgnoreCase)
                ? ViewModel.RefreshAsync()
                : ViewModel.LoadScenarioAsync(volume.RootPath);
    }

    /// <summary>
    /// Pushes scan state onto the shared bar. The label names the scope so the button says what it
    /// will actually do, and it reads the same before and after a scan — a button does not become a
    /// different button because it has been pressed, which is what the old "Rescan" claimed.
    /// </summary>
    private void RefreshScanBar()
    {
        if (ScanBar is null)
        {
            return;
        }

        ScanBar.IsScanning = IsScanning;
        ScanBar.StatusText = LiveScanText;
        ScanBar.Progress = ViewModel.Progress;
        ScanBar.CanScan = _selectedVolume is not null;
        ScanBar.ScanLabel = _selectedVolume is StorageVolume volume
            ? $"Scan {volume.DriveLetter}"
            : "Scan";
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        bool menu = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(CoreVirtualKeyStates.Down);

        // Through the command, never straight to RefreshAsync. RefreshCommand carries the
        // !IsScanning predicate that the scan button and every other caller respect; calling the
        // method directly walked around it, and Snapshot goes non-null about 100 ms into a scan when
        // the first partial lands. Held-down F5 auto-repeats ~30×/s, and each one cancelled and
        // relaunched a full-volume walk whose predecessor only stops at its next directory
        // boundary — dozens of concurrent walks fighting each other for the same disk, with the room
        // unable to settle for as long as the key was down.
        if (args.Key == VirtualKey.F5 &&
            ViewModel.Snapshot is not null &&
            ViewModel.RefreshCommand.CanExecute(null))
        {
            ViewModel.RefreshCommand.Execute(null);
            args.Handled = true;
        }
        else if (menu && args.Key == VirtualKey.Up)
        {
            ViewModel.NavigateUp();
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Enter &&
            ItemsList.SelectedItem is StorageRowViewModel { IsFolder: true } row)
        {
            ViewModel.NavigateTo(row.Id);
            args.Handled = true;
        }
    }

    /// <summary>
    /// Forwards each row's AutomationId onto the generated <c>ListViewItem</c>.
    /// </summary>
    /// <remarks>
    /// The id used to live on the template's layout-only root Grid, which surfaces as a UIA Group
    /// with no SelectionItem pattern -- reachable, but not selectable by that id. A screen reader
    /// (and any automation on a desktop where injected input is refused) selects through
    /// SelectionItem, so the id has to be on the container. <c>AutomationProperties.Name</c> stays
    /// on the template root, where it describes the row's contents.
    /// </remarks>
    private void Items_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not null && args.Item is StorageRowViewModel row)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(args.ItemContainer, row.AutomationId);
        }
    }
}
