using System.ComponentModel;
using CommunityToolkit.WinUI.Animations;
using DevDriveManager.Controls;
using DevDriveManager.ViewModels;
using DevDriveReclaim;
using DevDriveStorage;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;
using Windows.System;
using Windows.UI.Core;

namespace DevDriveManager.Pages;

/// <summary>Explores a user-requested scan; entering the room never starts disk I/O.</summary>
public sealed partial class SpacePage : Page, INotifyPropertyChanged
{
    private void TreeItem_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is TreeViewItem { Tag: StorageTreeItemViewModel item } container)
        {
            container.ContextFlyout = StorageContextMenu.Create(this, item.Node);
        }
    }

    private static readonly string[] DerivedProperties =
    [
        nameof(ShowOverlay),
        nameof(IsScanning),
        nameof(IsBusy),
        nameof(LiveScanText),
        nameof(OverlayTitle),
        nameof(OverlayDetail),
        nameof(OverlayGlyph),
    ];

    private readonly IVolumeProvider _volumes;
    private readonly Func<ReclaimConfirmationRequest, Task<bool>> _confirm;
    private List<SpaceCleanupGroup> _cleanupGroups = [];
    private bool _isLoaded;
    private bool _syncingTree;
    private bool _sourcesAttached;
    private string? _pendingVolumeLabel;
    private StorageVolume? _selectedVolume;
    private readonly UISettings _uiSettings = new();
    private readonly ImplicitAnimationSet _showAnimations;
    private readonly ImplicitAnimationSet _hideAnimations;

    public SpacePage() : this(App.SpaceVolumes)
    {
    }

    public SpacePage(IVolumeProvider volumes)
    {
        _volumes = volumes;
        ViewModel = App.SharedSpace;
        _confirm = request => ReclaimConfirmDialog.ShowAsync(XamlRoot, request);
        // Older visits may have left the app-lifetime projection in largest-files/search mode.
        // These are projection changes only, never a scan.
        ViewModel.Mode = ExplorerMode.Folders;
        ViewModel.SearchText = string.Empty;
        StripEntries = BuildStrip();
        InitializeComponent();
        SpaceProgressShadow.Receivers.Add(SpaceExplorerCard);
        _showAnimations = Implicit.GetShowAnimations(SpaceStatusBar);
        _hideAnimations = Implicit.GetHideAnimations(SpaceStatusBar);
        UpdateAnimations();
        Loaded += OnLoadedSubscribe;
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            Reclaim.PropertyChanged -= Reclaim_PropertyChanged;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _uiSettings.AnimationsEnabledChanged -= AnimationsEnabledChanged;
            DetachItemsSources();

            // A hook holding this page's XamlRoot would show the dialog on a window that is gone.
            if (Reclaim.ConfirmationRequested == _confirm)
            {
                Reclaim.ConfirmationRequested = null;
            }
        };
    }

    public StorageExplorerViewModel ViewModel { get; }

    /// <summary>The clean-up scan that runs beside the map's scan, shared with Home.</summary>
    public ReclaimViewModel Reclaim { get; } = App.SharedReclaim;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<VolumeStripEntry> StripEntries { get; }

    public bool ShowOverlay =>
        ViewModel.ScanState is ExplorerScanState.Idle or ExplorerScanState.Failed or ExplorerScanState.Cancelled ||
        (ViewModel.ScanState is ExplorerScanState.Scanning && ViewModel.Snapshot is null) ||
        (ViewModel.ScanState is ExplorerScanState.Completed && ViewModel.IsEmpty);

    public bool IsScanning => ViewModel.ScanState is ExplorerScanState.Scanning;

    /// <summary>Either scan is still running: the map's, or the clean-up check beside it.</summary>
    public bool IsBusy => IsScanning || Reclaim.IsScanning;

    public string LiveScanText => _pendingVolumeLabel is null
        ? "Scanning — results update as they are found"
        : $"Scanning {_pendingVolumeLabel} — results update as they are found";

    public string OverlayTitle => ViewModel.ScanState switch
    {
        ExplorerScanState.Scanning => _pendingVolumeLabel is null ? "Scanning" : $"Scanning {_pendingVolumeLabel}",
        ExplorerScanState.Failed => "That scan did not finish",
        ExplorerScanState.Cancelled => "Scan cancelled",
        ExplorerScanState.Completed => "No files found",
        _ when App.IsSpaceFixture => "Synthetic space test data — no disk scans",
        _ => StripEntries.Count == 0 ? "No drives found" : "Pick a drive to scan",
    };

    public string OverlayDetail => ViewModel.ScanState switch
    {
        ExplorerScanState.Scanning => ViewModel.ProgressLabel,
        ExplorerScanState.Failed => ViewModel.ErrorMessage ?? "No detail was reported.",
        ExplorerScanState.Cancelled => "Nothing was changed.",
        ExplorerScanState.Completed => "There is nothing here to show.",
        _ when StripEntries.Count == 0 => "Check that your drive is connected.",
        _ => "See what is using space on it.",
    };

    public string OverlayGlyph => ViewModel.ScanState switch
    {
        ExplorerScanState.Failed => "\uE783",
        ExplorerScanState.Cancelled => "\uE711",
        ExplorerScanState.Completed => "\uE8B7",
        _ => "\uEDA2",
    };

    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility OverlayMessageVisibility(bool isScanning) =>
        isScanning ? Visibility.Collapsed : Visibility.Visible;

    // The overlay is see-through, so hide the content under it rather than letting it bleed through.
    public static double ContentOpacity(bool showOverlay) => showOverlay ? 0 : 1;

    public static bool IsIndeterminate(double progress) => progress <= 0;

    public static Brush PaletteBrush(int colorIndex) => colorIndex < 0
        ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        : TreemapPalette.Resolve(colorIndex);

    public static string NodeGlyph(bool isFolder) => isFolder ? "\uE8D5" : "\uE8A5";

    public static string FreeSpaceDisplay(StorageVolume volume) => volume.IsSizeKnown
        ? $"{DevDriveCore.ByteSizeFormatter.Format((ulong)Math.Max(0, volume.FreeBytes))} free"
        : "Free space unavailable";

    // Safe is the normal case, so only the exceptions get a label.
    public static Visibility RiskLabelVisibility(ReclaimRisk risk) =>
        risk == ReclaimRisk.Safe ? Visibility.Collapsed : Visibility.Visible;

    public static string CleanupRowTip(string path, string reason) => $"{path}\n{reason}";

    public static string RevealName(string name) => $"Show {name} in the map";

    private void OnLoadedSubscribe(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Reclaim.PropertyChanged -= Reclaim_PropertyChanged;
        Reclaim.PropertyChanged += Reclaim_PropertyChanged;
        Reclaim.ConfirmationRequested = _confirm;
        FolderTree.ItemsSource = ViewModel.TreeRoots;
        StorageTreemap.Rows = ViewModel.VisibleItems;
        _sourcesAttached = true;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _uiSettings.AnimationsEnabledChanged -= AnimationsEnabledChanged;
            _uiSettings.AnimationsEnabledChanged += AnimationsEnabledChanged;
        }
        UpdateAnimations();
        UpdateDerived();
        SyncTreeSelection();
        RebuildCleanupGroups();
        UpdateCleanupPanel();
    }

    private void AnimationsEnabledChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(UpdateAnimations);

    private void UpdateAnimations()
    {
        if (_uiSettings.AnimationsEnabled)
        {
            Implicit.SetShowAnimations(SpaceStatusBar, _showAnimations);
            Implicit.SetHideAnimations(SpaceStatusBar, _hideAnimations);
        }
        else
        {
            SpaceStatusBar.ClearValue(Implicit.ShowAnimationsProperty);
            SpaceStatusBar.ClearValue(Implicit.HideAnimationsProperty);
            var visual = ElementCompositionPreview.GetElementVisual(SpaceStatusBar);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            visual.Opacity = 1;
            visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 0, 16));
        }
    }

    private void DetachItemsSources()
    {
        // Native collection listeners must not outlive the page. The shared VM keeps streaming
        // after navigation; leaving a torn-down TreeView subscribed can fail-fast.
        // Ignore selection removal during detach instead of clearing app-lifetime selection.
        _sourcesAttached = false;
        FolderTree.ItemsSource = null;
        StorageTreemap.Rows = null;
        CleanupList.ItemsSource = null;
    }

    private IReadOnlyList<VolumeStripEntry> BuildStrip()
    {
        try
        {
            return VolumeStrip.Build(_volumes.GetFixedVolumes(), Path.GetPathRoot(Environment.SystemDirectory));
        }
        catch (Exception)
        {
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
        string? selectedPath = ViewModel.IsScanning ? ViewModel.ScenarioId
            : ViewModel.SelectedVolumePath ?? ViewModel.ScenarioId;
        VolumeStripEntry? entry = StripEntries.FirstOrDefault(candidate => string.Equals(
            candidate.Volume.RootPath, selectedPath, StringComparison.OrdinalIgnoreCase));
        entry ??= VolumeStrip.DefaultScope(StripEntries);
        if (entry is not null)
        {
            _pendingVolumeLabel = entry.Volume.DisplayName;
            _selectedVolume = entry.Volume;
            VolumePicker.SelectedItem = entry;
            ViewModel.SelectedVolumePath = entry.Volume.RootPath;
        }

        UpdateDerived();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(StorageExplorerViewModel.SelectedRow):
                SyncTreeSelection();
                break;
            case nameof(StorageExplorerViewModel.ProjectionRevision):
                SyncTreeSelection();
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

    private void SyncTreeSelection()
    {
        if (!_sourcesAttached || _syncingTree)
        {
            return;
        }

        _syncingTree = true;
        try
        {
            FolderTree.SelectedItem = ViewModel.SelectedRow is { } row
                ? ViewModel.RevealTreeItem(row.Id)
                : null;
        }
        finally
        {
            _syncingTree = false;
        }
    }

    private void UpdateDerived()
    {
        foreach (string name in DerivedProperties)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        SpaceStatusBar.Visibility = BoolToVisibility(IsScanning);
        if (IsScanning && VolumePicker.SelectedItem is VolumeStripEntry selected &&
            !string.Equals(selected.Volume.RootPath, ViewModel.ScenarioId, StringComparison.OrdinalIgnoreCase))
        {
            VolumePicker.SelectedItem = StripEntries.FirstOrDefault(entry =>
                string.Equals(entry.Volume.RootPath, ViewModel.ScenarioId, StringComparison.OrdinalIgnoreCase));
        }
        if (!IsScanning && _selectedVolume is not null)
        {
            ViewModel.SelectedVolumePath = _selectedVolume.RootPath;
        }
        bool partial = ViewModel.Snapshot?.Completion == SnapshotCompletion.Partial;
        CoverageNotice.IsOpen = !IsScanning && ViewModel.Snapshot is not null &&
            (partial || ViewModel.HasCoverageDetail);
        CoverageNotice.Severity = partial ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        CoverageNotice.Title = ViewModel.CoverageDetail;
        CoverageNotice.Message = ViewModel.CoverageSummary;

        RefreshScanBar();
    }

    private void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is StorageTreeItemViewModel item)
        {
            _ = item.Children;
        }
    }

    private void FolderTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_sourcesAttached && !_syncingTree &&
            args.AddedItems.FirstOrDefault() is StorageTreeItemViewModel item)
        {
            _syncingTree = true;
            try
            {
                ViewModel.NavigateTreeItem(item.Id);
            }
            finally
            {
                _syncingTree = false;
            }
        }
    }

    // A folder anywhere in the map becomes the new scope; a file is only selected, since the map
    // already shows it in place and moving to its folder would re-flow everything around it.
    private void StorageTreemap_NodeInvoked(object sender, StorageNodeInvokedEventArgs args)
    {
        if (args.Node.Kind == StorageNodeKind.File)
        {
            ViewModel.Select(args.Node.Id);
        }
        else
        {
            ViewModel.NavigateTreeItem(args.Node.Id);
        }
    }

    private void StorageTreemap_NavigateUpRequested(object? sender, EventArgs args) => ViewModel.NavigateUp();

    private void CancelScanButton_Click(object sender, RoutedEventArgs args)
    {
        ViewModel.CancelScan();
        if (Reclaim.CancelScanCommand.CanExecute(null))
        {
            Reclaim.CancelScanCommand.Execute(null);
        }
    }

    private void RetryScanButton_Click(object sender, RoutedEventArgs args)
    {
        _ = ViewModel.RetryAsync();
        if (ViewModel.ScenarioId is { Length: > 0 } root)
        {
            StartCleanupScan(root);
        }
    }

    private void VolumePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (VolumePicker.SelectedItem is not VolumeStripEntry entry)
        {
            return;
        }
        _selectedVolume = entry.Volume;
        ViewModel.SelectedVolumePath = entry.Volume.RootPath;
        RefreshScanBar();
    }

    private void ScanButton_Click(object sender, RoutedEventArgs args)
    {
        if (IsBusy || Reclaim.IsReclaiming || _selectedVolume is not StorageVolume volume)
        {
            return;
        }

        _pendingVolumeLabel = volume.DisplayName;
        _ = ViewModel.Snapshot is not null &&
            string.Equals(ViewModel.ScenarioId, volume.RootPath, StringComparison.OrdinalIgnoreCase)
                ? ViewModel.RefreshAsync()
                : ViewModel.LoadScenarioAsync(volume.RootPath);
        StartCleanupScan(volume.RootPath);
    }

    /// <summary>
    /// Looks for things to clean up on the same drive, at the same time as the map scan.
    /// </summary>
    /// <remarks>
    /// The system drive only checks the usual code folders: walking all of <c>C:\</c> for repos means
    /// walking Windows too. Any other drive is checked from its root, because people clone straight
    /// into <c>D:\</c> and the usual folder names would find nothing there.
    /// </remarks>
    private void StartCleanupScan(string volumeRoot)
    {
        if (App.IsSpaceFixture || Reclaim.IsScanning || Reclaim.IsReclaiming)
        {
            return;
        }

        string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        bool isSystem = string.Equals(
            volumeRoot.TrimEnd('\\'), systemRoot?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        _ = Reclaim.ScanVolumeAsync(volumeRoot, includeWholeVolume: !isSystem);
    }

    private void Reclaim_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(ReclaimViewModel.IsScanning):
                UpdateDerived();
                RebuildCleanupGroups();
                break;
            case nameof(ReclaimViewModel.IsReclaiming):
                RefreshScanBar();
                break;
            case nameof(ReclaimViewModel.HasScanned):
            case nameof(ReclaimViewModel.FoundBytes):
                RebuildCleanupGroups();
                break;
        }

        UpdateCleanupPanel();
    }

    /// <summary>
    /// Rebuilds the grouped list from the categories that found something, largest first.
    /// </summary>
    /// <remarks>
    /// A plain list, copied from the ViewModel's collections, so no native collection listener is
    /// left on the app-lifetime ViewModel when the page goes away. Rows only change when a scan or a
    /// clean-up finishes, and both are covered by the properties that call this.
    /// </remarks>
    private void RebuildCleanupGroups()
    {
        if (!_sourcesAttached)
        {
            return;
        }

        HashSet<string> expanded = [.. _cleanupGroups.Where(g => g.IsExpanded).Select(g => g.Id)];
        bool fresh = _cleanupGroups.Count == 0;

        _cleanupGroups = Reclaim.IsScanning
            ? []
            : [.. Reclaim.Categories
                .Where(c => c.Rows.Count > 0)
                .OrderByDescending(c => c.TotalBytes)
                .Select(c => new SpaceCleanupGroup(c, expanded.Contains(c.Category.Id)))];

        // Open the biggest group on a new result, so the list says what was found without a click.
        if (fresh && _cleanupGroups.Count > 0)
        {
            _cleanupGroups[0].IsExpanded = true;
        }

        CleanupList.ItemsSource = _cleanupGroups;
        CleanupScroller.Visibility = BoolToVisibility(_cleanupGroups.Count > 0);
    }

    private void UpdateCleanupPanel()
    {
        bool scanning = Reclaim.IsScanning;
        bool reclaiming = Reclaim.IsReclaiming;
        bool failed = !scanning && Reclaim.ScanError is not null;
        bool found = !scanning && _cleanupGroups.Count > 0;
        string scope = Reclaim.ScopeText;

        CleanupHeadline.Text = Reclaim.FoundBytesText;
        CleanupHeadline.Visibility = BoolToVisibility(found);

        CleanupDetail.Text = scanning ? $"Looking for things to clean up on {scope}…"
            : failed ? $"The clean-up check did not finish. {Reclaim.ScanError}"
            : found ? $"can be freed on {scope}"
            : string.Empty;
        CleanupDetail.Visibility = BoolToVisibility(CleanupDetail.Text.Length > 0);
        CleanupProgress.Visibility = BoolToVisibility(scanning || reclaiming);

        string? summary = reclaiming ? Reclaim.ReclaimProgressText : Reclaim.LastRunSummary;
        CleanupRunSummary.Text = summary ?? string.Empty;
        CleanupRunSummary.Visibility = BoolToVisibility(!scanning && !string.IsNullOrEmpty(summary));
        CleanupRunSummary.Foreground = (Brush)Application.Current.Resources[
            Reclaim.LastRunHadFailures && !reclaiming ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];

        bool empty = !scanning && !failed && !found;
        CleanupEmpty.Visibility = BoolToVisibility(empty);
        if (empty)
        {
            CleanupEmptyIcon.Glyph = Reclaim.HasScanned ? "\uE73E" : "\uE721";
            CleanupEmptyText.Text = Reclaim.HasScanned
                ? $"Nothing to clean up on {scope}."
                : "Scan a drive to see what you can remove.";
        }

        CleanupFooter.Visibility = BoolToVisibility(found || reclaiming);
        CleanupButton.Content = reclaiming ? "Removing…"
            : Reclaim.SelectedBytes > 0 ? $"Clean up {Reclaim.SelectedBytesText}"
            : "Clean up";
    }

    /// <summary>Shows a clean-up item where it sits in the map, beside the things around it.</summary>
    private void CleanupRow_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: ReclaimRowViewModel row } ||
            ViewModel.Snapshot is not { } snapshot ||
            FindNode(snapshot, row.Path) is not { } node)
        {
            return;
        }

        if (node.ParentId is Guid parent)
        {
            ViewModel.NavigateTo(parent);
        }

        ViewModel.Select(node.Id);
    }

    /// <summary>
    /// The node for <paramref name="path"/>, or the deepest folder the map has above it. Null when the
    /// path is not on the scanned drive at all.
    /// </summary>
    private static StorageNode? FindNode(StorageSnapshot snapshot, string path)
    {
        string target = path.TrimEnd('\\');
        StorageNode current = snapshot.Root;
        if (!IsSameOrUnder(target, current.PhysicalPath))
        {
            return null;
        }

        while (!string.Equals(current.PhysicalPath.TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase))
        {
            StorageNode? next = snapshot.ChildrenOf(current.Id)
                .FirstOrDefault(child => IsSameOrUnder(target, child.PhysicalPath));
            if (next is null)
            {
                break;
            }

            current = next;
        }

        return current;
    }

    private static bool IsSameOrUnder(string target, string candidate)
    {
        string root = candidate.TrimEnd('\\');
        return root.Length > 0 &&
            (string.Equals(target, root, StringComparison.OrdinalIgnoreCase) ||
             target.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshScanBar()
    {
        ScanButton.IsEnabled = !IsBusy && !Reclaim.IsReclaiming && _selectedVolume is not null;
        VolumePicker.IsEnabled = !IsBusy && StripEntries.Count > 0;
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        bool menu = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        if (args.Key == VirtualKey.F5 && !IsBusy && _selectedVolume is not null)
        {
            ScanButton_Click(sender, args);
            args.Handled = true;
        }
        else if (menu && args.Key == VirtualKey.Up)
        {
            ViewModel.NavigateUp();
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Enter &&
            ViewModel.SelectedRow is StorageRowViewModel { IsFolder: true } row)
        {
            ViewModel.NavigateTo(row.Id);
            args.Handled = true;
        }
    }
}

/// <summary>One kind of thing the clean-up check found, shown as a group in the Space panel.</summary>
public sealed class SpaceCleanupGroup(ReclaimCategoryViewModel category, bool isExpanded)
{
    public string Id { get; } = category.Category.Id;

    public string Title { get; } = category.Title;

    public string Glyph { get; } = category.Glyph;

    public string TotalText { get; } = category.TotalText;

    // A copy, so the page never listens to the shared ViewModel's collection.
    public IReadOnlyList<ReclaimRowViewModel> Rows { get; } = [.. category.Rows];

    public bool IsExpanded { get; set; } = isExpanded;

    public string AutomationId => $"SpaceCleanupGroup_{Id}";

    public string AutomationName => $"{Title}, {TotalText}, {Rows.Count} {(Rows.Count == 1 ? "item" : "items")}";
}
