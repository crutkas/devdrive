using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using DevDriveCore.Models;
using DevDriveCore.Services;
using DevDriveManager.Services;
using DevDriveManager.ViewModels;
using DevDriveReclaim;
using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Pages;

/// <summary>
/// The Home room: how each drive looks — used, reclaimable, free — and the few things worth doing
/// about it, chiefly whether this PC should have a Dev Drive and what should move onto it.
/// </summary>
/// <remarks>
/// <para>
/// Overview owns no data of its own. Every figure is assembled from what the other rooms already
/// know — volumes, the reclaim scan, the cache inventory — which is deliberate: a landing page that
/// scanned on its own would either duplicate work the rooms do or disagree with them, and
/// disagreeing is worse.
/// </para>
/// </remarks>
public sealed partial class DashboardPage : Page, INotifyPropertyChanged
{
    private readonly ObservableCollection<DriveCardViewModel> _drives = [];
    private readonly ObservableCollection<AttentionSignalViewModel> _opportunities = [];

    private bool _refreshQueued;

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainPageViewModel ViewModel => App.Shared;

    /// <summary>The reclaim scan, shared with its own room so a scan started here survives navigation.</summary>
    public ReclaimViewModel Reclaim => App.SharedReclaim;

    public StorageExplorerViewModel Space => App.SharedSpace;

    public IReadOnlyList<DriveCardViewModel> DriveCards => _drives;

    /// <summary>
    /// Never more columns than drives, so a PC with three drives fills the row with three cards
    /// instead of leaving an empty slot at the end.
    /// </summary>
    public int DriveColumns => Math.Max(1, _drives.Count);

    public IReadOnlyList<AttentionSignalViewModel> Opportunities => _opportunities;

    public bool HasOpportunities => _opportunities.Count > 0;

    /// <summary>The number on the badge beside the Opportunities heading.</summary>
    public int OpportunityCount => _opportunities.Count;

    // ---- Empty state -------------------------------------------------------------------------

    public string EmptyTitle => ViewModel.Volumes.Count == 0
        ? "Reading drives…"
        : Reclaim.IsScanning
            ? "Looking for space you can get back…"
            : Reclaim.HasScanned ? "You're all set" : "Nothing to suggest yet";

    public string EmptyDetail => ViewModel.Volumes.Count == 0
        ? string.Empty
        : Reclaim.IsScanning
            ? "Suggestions appear here when the scan finishes."
            : Reclaim.HasScanned
                ? "Your Dev Drive is set up, your caches are on it, and the last scan found nothing worth removing."
                : "Your Dev Drive is set up. Scan this PC to find out how much space you could get back.";

    // ---- Lifecycle ---------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Volumes.CollectionChanged += OnVolumesChanged;
        ViewModel.PackageCaches.CachesChanged += OnSourceChanged;
        ViewModel.PackageCaches.InventoryReset += OnSourceChanged;
        Reclaim.PropertyChanged += OnReclaimChanged;
        Space.PropertyChanged += OnSpaceChanged;
        PreferencesService.Changed += OnPreferencesChanged;

        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Volumes.CollectionChanged -= OnVolumesChanged;
        ViewModel.PackageCaches.CachesChanged -= OnSourceChanged;
        ViewModel.PackageCaches.InventoryReset -= OnSourceChanged;
        Reclaim.PropertyChanged -= OnReclaimChanged;
        Space.PropertyChanged -= OnSpaceChanged;
        PreferencesService.Changed -= OnPreferencesChanged;
    }

    // The low-space threshold and the cache watch both change what this room says.
    private void OnPreferencesChanged(object? sender, EventArgs e) => RequestRefresh();

    private void OnVolumesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        RequestRefresh();

    private void OnSourceChanged(object? sender, EventArgs e) => RequestRefresh();

    /// <summary>
    /// Only the four properties this room reads. A reclaim scan raises hundreds of changes as rows
    /// stream in, and rebuilding the whole room on each one would rebuild it hundreds of times.
    /// </summary>
    private void OnReclaimChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReclaimViewModel.FoundBytes)
            or nameof(ReclaimViewModel.FoundRiskMix)
            or nameof(ReclaimViewModel.HasScanned)
            or nameof(ReclaimViewModel.IsScanning))
        {
            RequestRefresh();
        }
    }

    private void OnSpaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StorageExplorerViewModel.Snapshot))
        {
            RequestRefresh();
        }
    }

    /// <summary>
    /// Coalesces the rebuild onto one dispatcher tick. Several independent sources feed this room and
    /// a single load moves all of them, so without this the room rebuilds once per source per load.
    /// </summary>
    private void RequestRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        RebuildDrives();
        RebuildOpportunities();

        Raise(nameof(EmptyTitle));
        Raise(nameof(EmptyDetail));
    }

    // ---- Drives ------------------------------------------------------------------------------

    private void RebuildDrives()
    {
        double lowFraction = PreferencesService.Current.LowFreeFraction;

        List<DriveCardViewModel> cards = [];
        foreach (VolumeRowViewModel row in ViewModel.Volumes)
        {
            cards.Add(new DriveCardViewModel(row, lowFraction));
        }

        // Rebuilding the cards recreates their elements; skip it when nothing a reader can see moved.
        if (cards.Select(Signature).SequenceEqual(_drives.Select(Signature)))
        {
            return;
        }

        _drives.Clear();
        foreach (DriveCardViewModel card in cards)
        {
            _drives.Add(card);
        }

        Raise(nameof(DriveColumns));

        static string Signature(DriveCardViewModel card) =>
            $"{card.AutomationId}|{card.AutomationName}|{card.Subtitle}|{card.BadgeText}";
    }

    // ---- Opportunities -----------------------------------------------------------------------

    private void RebuildOpportunities()
    {
        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(new AttentionInputs
        {
            Volumes = [.. ViewModel.Volumes.Select(row => row.Volume)],
            HasReclaimScan = Reclaim.HasScanned,
            ReclaimableBytes = Reclaim.FoundBytes,
            ReclaimSafeBytes = Reclaim.FoundRiskMix.SafeBytes,
            ReclaimCategoryCount = Reclaim.Categories.Count(c => c.TotalBytes > 0),
            CachesOffDevDrive = [.. ViewModel.PackageCaches.Caches
                .Where(row => row.Info.Detected && !row.IsOnDevDrive)
                .Select(row => new CacheSignalInput(
                    row.Header, row.LocationText, (long)row.SizeBytes, row.IsRedirected))],
            CachesOnDevDrive = ViewModel.PackageCaches.Caches.Count(row => row.IsOnDevDrive),
            HasSpaceScan = Space.Snapshot is not null,
            LowFreeFraction = PreferencesService.Current.LowFreeFraction,
            CacheRollupThreshold = PreferencesService.Current.CacheRollupThreshold,
            WatchCachesOffDevDrive = PreferencesService.Current.WatchCachesOffDevDrive,
        });

        // Low space is told by the drive card it belongs to, and the advisory "run a scan" by the
        // header's own Scan button; what is left is what this PC could gain.
        AttentionSignalViewModel[] opportunities = [.. signals
            .Where(s => !s.IsAdvisory && !s.Id.StartsWith("LowSpace_", StringComparison.Ordinal))
            .Select(s => new AttentionSignalViewModel(s))];

        if (opportunities.Select(Signature).SequenceEqual(_opportunities.Select(Signature)))
        {
            return;
        }

        _opportunities.Clear();
        foreach (AttentionSignalViewModel opportunity in opportunities)
        {
            _opportunities.Add(opportunity);
        }

        Raise(nameof(HasOpportunities));
        Raise(nameof(OpportunityCount));

        static string Signature(AttentionSignalViewModel row) => $"{row.AutomationName}|{row.ActionText}";
    }

    /// <summary>
    /// Overview scans the whole machine, which is the same act Reclaim performs, so it runs the
    /// same command rather than a parallel copy of it.
    /// </summary>
    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (Reclaim.ScanCommand.CanExecute(null))
        {
            Reclaim.ScanCommand.Execute(null);
        }
    }

    private void CancelScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (Reclaim.CancelScanCommand.CanExecute(null))
        {
            Reclaim.CancelScanCommand.Execute(null);
        }
    }

    /// <summary>Opens the room that acts on a card, named by the button's tag.</summary>
    private void Room_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && tag.Length > 0)
        {
            ShellPage.Current?.SelectNavItem(tag);
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
