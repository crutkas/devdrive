using System.ComponentModel;
using CommunityToolkit.WinUI.Animations;
using DevDriveManager.Controls;
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
        nameof(LiveScanText),
        nameof(OverlayTitle),
        nameof(OverlayDetail),
    ];

    private readonly IVolumeProvider _volumes;
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
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _uiSettings.AnimationsEnabledChanged -= AnimationsEnabledChanged;
            DetachItemsSources();
        };
    }

    public StorageExplorerViewModel ViewModel { get; }

    public string PageTitle => RoomRegistry.Find("space")!.Title;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<VolumeStripEntry> StripEntries { get; }

    public bool ShowOverlay =>
        ViewModel.ScanState is ExplorerScanState.Idle or ExplorerScanState.Failed or ExplorerScanState.Cancelled ||
        (ViewModel.ScanState is ExplorerScanState.Scanning && ViewModel.Snapshot is null) ||
        (ViewModel.ScanState is ExplorerScanState.Completed && ViewModel.IsEmpty);

    public bool IsScanning => ViewModel.ScanState is ExplorerScanState.Scanning;

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
        _ => StripEntries.Count == 0 ? "No fixed volumes available" : "Pick a volume to scan",
    };

    public string OverlayDetail => ViewModel.ScanState switch
    {
        ExplorerScanState.Scanning => ViewModel.ProgressLabel,
        ExplorerScanState.Failed => ViewModel.ErrorMessage ?? "No detail was reported.",
        ExplorerScanState.Cancelled => "Nothing was changed. Pick a volume above to start again.",
        ExplorerScanState.Completed => "This scope contains no readable files or folders.",
        _ when StripEntries.Count == 0 => "No fixed volumes could be read. Check that your drive is connected and available.",
        _ => "Choose a volume above and select Scan. A first scan reads every directory on the "
            + "volume, so a large drive can take several minutes — you can cancel at any point.",
    };

    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility OverlayMessageVisibility(bool isScanning) =>
        isScanning ? Visibility.Collapsed : Visibility.Visible;

    // The native card fill is translucent; don't let the treemap's empty label bleed through it.
    public static double ContentOpacity(bool showOverlay) => showOverlay ? 0 : 1;

    public static bool IsIndeterminate(double progress) => progress <= 0;

    public static Brush PaletteBrush(int colorIndex) => colorIndex < 0
        ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        : TreemapPalette.Resolve(colorIndex);

    public static string NodeGlyph(bool isFolder) => isFolder ? "\uE8D5" : "\uE8A5";

    public static string FreeSpaceDisplay(StorageVolume volume) => volume.IsSizeKnown
        ? $"{DevDriveCore.ByteSizeFormatter.Format((ulong)Math.Max(0, volume.FreeBytes))} free"
        : "Free space unavailable";

    private void OnLoadedSubscribe(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
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

    private void CancelScanButton_Click(object sender, RoutedEventArgs args) => ViewModel.CancelScan();
    private void RetryScanButton_Click(object sender, RoutedEventArgs args) => _ = ViewModel.RetryAsync();
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
        if (IsScanning || _selectedVolume is not StorageVolume volume)
        {
            return;
        }

        _pendingVolumeLabel = volume.DisplayName;
        _ = ViewModel.Snapshot is not null &&
            string.Equals(ViewModel.ScenarioId, volume.RootPath, StringComparison.OrdinalIgnoreCase)
                ? ViewModel.RefreshAsync()
                : ViewModel.LoadScenarioAsync(volume.RootPath);
    }

    private void RefreshScanBar()
    {
        ScanButton.IsEnabled = !IsScanning && _selectedVolume is not null;
        VolumePicker.IsEnabled = !IsScanning && StripEntries.Count > 0;
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        bool menu = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        if (args.Key == VirtualKey.F5 && !IsScanning && _selectedVolume is not null)
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
