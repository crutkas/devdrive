using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using DevDriveCore;
using DevDriveCore.Models;
using DevDriveCore.Services;
using DevDriveManager.Controls;
using DevDriveManager.ViewModels;
using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

// Both libraries define a ByteSizeFormatter. Core takes ulong and steps by 1024, Storage takes long
// and steps by 1000; this room reads volume bytes (ulong) from one and node bytes (long) from the
// other, so both are needed and neither can be the unqualified name.
using CoreBytes = DevDriveCore.ByteSizeFormatter;
using StorageBytes = DevDriveStorage.ByteSizeFormatter;

namespace DevDriveManager.Pages;

/// <summary>
/// The Overview room: what this machine needs, ranked, and where each of those things gets done.
/// </summary>
/// <remarks>
/// <para>
/// Overview owns no data of its own. Every signal is assembled from what the other rooms already
/// know — volumes, the reclaim scan, the cache inventory, the space snapshot — which is deliberate:
/// a landing page that scanned on its own would either duplicate work the rooms do or disagree with
/// them, and disagreeing is worse.
/// </para>
/// <para>
/// The one exception is the free-space history, which no room owns because no room is the right
/// place to keep it. Windows publishes free space but not its past, so the app records a reading
/// each time volumes are read and keeps them in the user's local app data.
/// </para>
/// </remarks>
public sealed partial class DashboardPage : Page, INotifyPropertyChanged
{
    /// <summary>
    /// Everything in a signal row that is not the signal column, in DIPs: WHERE, IMPACT and GOES TO
    /// (168 + 104 + 92), the three 12px gaps, the row's 14px horizontal padding, and the card border.
    /// </summary>
    private const double SignalFixedColumnsWidth = 168 + 104 + 92 + (12 * 3) + 28 + 2;

    /// <summary>
    /// How far back the chart looks. Thirty days is the window the room's caption promises; readings
    /// older than that are kept on disk but not plotted, so a machine that sat idle for a quarter
    /// does not draw a flat line across three months.
    /// </summary>
    private static readonly TimeSpan TrendWindow = TimeSpan.FromDays(30);

    private readonly IVolumeProvider _volumeProvider = new SystemVolumeProvider();
    private readonly IFreeSpaceHistoryStore _history = new JsonFreeSpaceHistoryStore();
    private readonly ObservableCollection<VolumeStripEntry> _volumeStripEntries = [];
    private readonly ObservableCollection<AttentionSignalViewModel> _signals = [];
    private readonly ObservableCollection<InspectorFactViewModel> _biggest = [];
    private readonly ObservableCollection<InspectorFactViewModel> _coverage = [];

    private FreeSpaceTrend _trend = FreeSpaceTrend.Empty("—");
    private AttentionSignalViewModel? _selectedSignal;
    private bool _refreshQueued;
    private double _cardWidth;
    private double _signalColumnWidth = 360;

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

    public IReadOnlyList<VolumeStripEntry> VolumeStripEntries => _volumeStripEntries;

    public IReadOnlyList<AttentionSignalViewModel> Signals => _signals;

    public IReadOnlyList<InspectorFactViewModel> BiggestFolders => _biggest;

    public IReadOnlyList<InspectorFactViewModel> CoverageFacts => _coverage;

    /// <summary>Width of the flexible first column, shared by the header and every row.</summary>
    public GridLength SignalColumnWidth => new(_signalColumnWidth);

    public bool HasSignals => _signals.Count > 0;

    /// <summary>
    /// The selected row, which the inspector explains. Set through the property rather than only in
    /// XAML so a rebuild can re-point it at the equivalent live row.
    /// </summary>
    public AttentionSignalViewModel? SelectedSignal
    {
        get => _selectedSignal;
        set
        {
            if (ReferenceEquals(value, _selectedSignal))
            {
                return;
            }

            _selectedSignal = value;
            RaiseInspector();
        }
    }

    // ---- Head text ---------------------------------------------------------------------------

    public string ScanButtonText => Reclaim.IsScanning ? "Scanning…" : "Scan this PC";

    public string SignalsSubtitle => _signals.Count switch
    {
        0 => "nothing outstanding",
        1 => "1 signal",
        _ => $"{_signals.Count} signals, ranked by what you get back",
    };

    /// <summary>
    /// What the app has actually looked at. Two scans feed this room and they run independently, so
    /// the chip names whichever has run rather than implying one "last scan" that covers both.
    /// </summary>
    public string LastScanText
    {
        get
        {
            if (Reclaim.IsScanning)
            {
                return "Scanning now";
            }

            if (Space.Snapshot is StorageSnapshot snapshot)
            {
                return $"Space scanned {Ago(snapshot.CapturedAtUtc)}";
            }

            return Reclaim.HasScanned ? "Reclaim scanned this session" : "Nothing scanned yet";
        }
    }

    public string EmptyTitle => Reclaim.HasScanned
        ? "Nothing needs attention"
        : "Nothing to report yet";

    public string EmptyDetail => Reclaim.HasScanned
        ? "Every volume has headroom, the caches are where they should be, and the last scan found "
            + "nothing worth deleting."
        : "Volumes look healthy. Run a scan to find out what is actually reclaimable — until then "
            + "this room only knows what the volume table tells it.";

    // ---- The chart ---------------------------------------------------------------------------

    public bool HasTrend => _trend.HasTrend;

    public string TrendSubtitle
    {
        get
        {
            if (!_trend.HasTrend)
            {
                return _trend.VolumeId;
            }

            long delta = _trend.DeltaBytes;
            string direction = delta switch
            {
                < 0 => $"down {CoreBytes.Format((ulong)(-delta))}",
                > 0 => $"up {CoreBytes.Format((ulong)delta)}",
                _ => "unchanged",
            };

            return $"{_trend.VolumeId} — {direction} over {Span(_trend.Span)}";
        }
    }

    public string TrendOldestText => _trend.Oldest is FreeSpaceSample oldest
        ? $"{Ago(oldest.TakenAtUtc)} — {CoreBytes.Format((ulong)oldest.FreeBytes)}"
        : string.Empty;

    public string TrendLatestText => _trend.Latest is FreeSpaceSample latest
        ? $"now — {CoreBytes.Format((ulong)latest.FreeBytes)}"
        : string.Empty;

    public string TrendProjectedText => _trend.ProjectedFreeBytes is long projected
        ? $"in {Span(_trend.Span)} at this rate — {CoreBytes.Format((ulong)projected)}"
        : string.Empty;

    /// <summary>
    /// The chart has no rows for a screen reader to walk, so the whole thing reads as one sentence.
    /// </summary>
    public string TrendAccessibleName => _trend.HasTrend
        ? $"{TrendSubtitle}. {TrendOldestText}. {TrendLatestText}. {TrendProjectedText}."
        : "Free space chart, not enough history yet";

    public string TrendEmptyDetail => _trend.SampleCount switch
    {
        0 => "Windows reports how much space is free, but not what it was yesterday. This app started "
            + "keeping its own record just now — the chart appears once there is a second reading.",
        _ => "One reading so far, taken when the app first read this machine. A second one is due "
            + $"{Span(FreeSpaceHistory.MinimumInterval)} after the first; the chart appears then.",
    };

    // ---- The inspector -----------------------------------------------------------------------

    public string InspectorTitle => _selectedSignal is null ? "This machine" : "About this signal";

    public string MetricOneLabel => _selectedSignal is null ? "VOLUMES" : "IMPACT";

    public string MetricOneValue => _selectedSignal is null
        ? ViewModel.Volumes.Count.ToString("N0")
        : _selectedSignal.Impact;

    public string MetricTwoLabel => _selectedSignal is null ? "RECLAIMABLE" : "GOES TO";

    public string MetricTwoValue => _selectedSignal is null
        ? Reclaim.HasScanned ? ReclaimFormat.Bytes(Reclaim.FoundBytes) : "—"
        : _selectedSignal.RoomLabel;

    public string SelectionLabel => _selectedSignal is null ? "WHAT THIS ROOM IS" : "WHY IT IS LISTED";

    public string SelectionDetail => _selectedSignal is null
        ? "Everything below is measured, not estimated. Select a row to see what it means and which "
            + "room acts on it."
        : $"{_selectedSignal.Detail}. Found on {_selectedSignal.Where}.";

    public string BiggestLabel => _biggest.Count > 0 ? "BIGGEST FOLDERS SCANNED" : "BIGGEST FOLDERS";

    /// <summary>
    /// Whether the biggest-folders block has anything to show. A section heading over an empty list
    /// reads as data the app failed to load rather than as a scan that has not run.
    /// </summary>
    public bool HasBiggestFolders => _biggest.Count > 0;

    // ---- Lifecycle ---------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Volumes.CollectionChanged += OnVolumesChanged;
        ViewModel.PackageCaches.CachesChanged += OnSourceChanged;
        ViewModel.PackageCaches.InventoryReset += OnSourceChanged;
        Reclaim.PropertyChanged += OnReclaimChanged;
        Space.PropertyChanged += OnSpaceChanged;

        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Volumes.CollectionChanged -= OnVolumesChanged;
        ViewModel.PackageCaches.CachesChanged -= OnSourceChanged;
        ViewModel.PackageCaches.InventoryReset -= OnSourceChanged;
        Reclaim.PropertyChanged -= OnReclaimChanged;
        Space.PropertyChanged -= OnSpaceChanged;
    }

    private void OnVolumesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        RequestRefresh();

    private void OnSourceChanged(object? sender, System.EventArgs e) => RequestRefresh();

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
    /// Coalesces the rebuild onto one dispatcher tick. Four independent sources feed this room and a
    /// single load moves all of them, so without this the room rebuilds — and re-enumerates every
    /// volume — once per source per load.
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
        RefreshVolumeStrip();
        RecordFreeSpace();
        RebuildSignals();
        RebuildInspector();
        UpdateStatusBar();

        Raise(nameof(ScanButtonText));
        Raise(nameof(SignalsSubtitle));
        Raise(nameof(LastScanText));
        Raise(nameof(EmptyTitle));
        Raise(nameof(EmptyDetail));
    }

    // ---- Signals -----------------------------------------------------------------------------

    private void RebuildSignals()
    {
        string? previous = _selectedSignal?.Signal.Id;

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
        });

        _signals.Clear();
        foreach (AttentionSignal signal in signals)
        {
            _signals.Add(new AttentionSignalViewModel(signal));
        }

        // A rebuild replaces every row object, so a selection held by reference would point at a row
        // that is no longer in the list: nothing highlighted, and an inspector still describing the
        // previous scan.
        _selectedSignal = previous is null
            ? null
            : _signals.FirstOrDefault(row => row.Signal.Id == previous);

        Raise(nameof(Signals));
        Raise(nameof(HasSignals));
        Raise(nameof(SelectedSignal));
        RaiseInspector();
    }

    /// <summary>
    /// Opens the room a signal belongs to. Selecting a row explains it; this is the separate gesture
    /// that acts on it, so reading the explanation never costs the reader their place.
    /// </summary>
    private void Room_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && tag.Length > 0)
        {
            ShellPage.Current?.SelectNavItem(tag);
        }
    }

    /// <summary>Enter opens the selected row's room — the same promise the status bar makes.</summary>
    private void Signals_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _selectedSignal is AttentionSignalViewModel row)
        {
            e.Handled = true;
            ShellPage.Current?.SelectNavItem(row.RoomTag);
        }
    }

    // ---- The chart ---------------------------------------------------------------------------

    /// <summary>
    /// Records one reading per volume and re-reads the trend. Throttling lives in the store, so
    /// calling this on every refresh costs a file read and nothing else.
    /// </summary>
    private void RecordFreeSpace()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<FreeSpaceSample> history = _history.Load();

        foreach (VolumeRowViewModel row in ViewModel.Volumes.Where(r => r.Volume.SizeBytes > 0))
        {
            history = _history.Append(new FreeSpaceSample
            {
                TakenAtUtc = now,
                VolumeId = VolumeId(row.Volume),
                TotalBytes = (long)row.Volume.SizeBytes,
                FreeBytes = (long)row.Volume.FreeBytes,
            });
        }

        // The Dev Drive is the volume this app is about; without one, the system volume is the one
        // whose free space anyone is actually watching.
        VolumeRowViewModel? subject = ViewModel.Volumes.FirstOrDefault(r => r.IsDevDrive)
            ?? ViewModel.Volumes.FirstOrDefault(IsSystemRow)
            ?? ViewModel.Volumes.FirstOrDefault();

        _trend = subject is null
            ? FreeSpaceTrend.Empty("—")
            : FreeSpaceHistory.Describe(history, VolumeId(subject.Volume), now, TrendWindow);

        DrawSpark();

        Raise(nameof(HasTrend));
        Raise(nameof(TrendSubtitle));
        Raise(nameof(TrendOldestText));
        Raise(nameof(TrendLatestText));
        Raise(nameof(TrendProjectedText));
        Raise(nameof(TrendEmptyDetail));
        Raise(nameof(TrendAccessibleName));
    }

    private void SparkHost_SizeChanged(object sender, SizeChangedEventArgs e) => DrawSpark();

    /// <summary>
    /// Lays the normalised points out in the plot area. The model hands back a unit square; this is
    /// the only place that knows how big the card is.
    /// </summary>
    private void DrawSpark()
    {
        SparkMeasured.Points.Clear();
        SparkProjected.Points.Clear();
        SparkFill.Points.Clear();

        double width = SparkHost.ActualWidth;
        double height = SparkHost.ActualHeight;
        if (!_trend.HasTrend || width <= 1 || height <= 1)
        {
            return;
        }

        // Inset by the stroke so the top and bottom of the line are not clipped by the plot edge.
        const double inset = 3;
        double plotHeight = Math.Max(1, height - (inset * 2));

        Windows.Foundation.Point At(FreeSpacePoint point) => new(
            point.X * width,
            inset + ((1 - point.Y) * plotHeight));

        FreeSpacePoint[] measured = [.. _trend.Points.Where(p => !p.IsProjected)];
        foreach (FreeSpacePoint point in measured)
        {
            SparkMeasured.Points.Add(At(point));
            SparkFill.Points.Add(At(point));
        }

        // The fill closes down the right edge and back along the bottom, so the area under the
        // measured line reads as volume rather than as a second, thicker line.
        if (measured.Length > 0)
        {
            SparkFill.Points.Add(new Windows.Foundation.Point(measured[^1].X * width, height));
            SparkFill.Points.Add(new Windows.Foundation.Point(measured[0].X * width, height));
        }

        if (_trend.Points.FirstOrDefault(p => p.IsProjected) is FreeSpacePoint projected
            && measured.Length > 0)
        {
            // Starts at the last measured point, so the dashed segment continues the solid one
            // instead of floating beside it.
            SparkProjected.Points.Add(At(measured[^1]));
            SparkProjected.Points.Add(At(projected));
        }
    }

    // ---- The inspector -----------------------------------------------------------------------

    private void RebuildInspector()
    {
        _biggest.Clear();
        _coverage.Clear();

        if (Space.Snapshot is StorageSnapshot snapshot)
        {
            foreach (StorageNode node in snapshot
                .ChildrenOf(snapshot.RootId)
                .Where(n => n.Kind == StorageNodeKind.Folder)
                .OrderByDescending(n => n.SizeBytes)
                .Take(4))
            {
                _biggest.Add(new InspectorFactViewModel(node.Name, node.SizeDisplay));
            }

            _coverage.Add(new InspectorFactViewModel(
                "Nodes walked", snapshot.Nodes.Length.ToString("N0"), AutomationId: "CoverageNodes"));

            _coverage.Add(new InspectorFactViewModel(
                "Coverage",
                snapshot.Coverage.Ratio.ToString("P0"),
                snapshot.Completion == SnapshotCompletion.Complete ? "complete" : "partial",
                "CoverageRatio"));

            _coverage.Add(new InspectorFactViewModel(
                "Paths denied",
                snapshot.Coverage.DeniedPaths.Length.ToString("N0"),
                AutomationId: "CoverageDenied"));

            // Only when the source computed both aggregates — the mock path leaves them null, and a
            // "0 B of slack" line would be a measurement nobody took.
            if (snapshot.Coverage.AllocatedMinusApparentBytes is long slack && slack > 0)
            {
                _coverage.Add(new InspectorFactViewModel(
                    "Cluster slack",
                    StorageBytes.Format(slack),
                    AutomationId: "CoverageSlack"));
            }
        }
        else
        {
            _coverage.Add(new InspectorFactViewModel(
                "Nodes walked", "—", "no scan yet", "CoverageNodes"));
        }

        if (Reclaim.HasScanned)
        {
            _coverage.Add(new InspectorFactViewModel(
                "Reclaim categories",
                $"{Reclaim.Categories.Count(c => c.TotalBytes > 0)} of {Reclaim.Categories.Count}",
                "found something",
                "CoverageCategories"));
        }

        Raise(nameof(BiggestFolders));
        Raise(nameof(HasBiggestFolders));
        Raise(nameof(CoverageFacts));
        Raise(nameof(BiggestLabel));
    }

    private void RaiseInspector()
    {
        Raise(nameof(InspectorTitle));
        Raise(nameof(MetricOneLabel));
        Raise(nameof(MetricOneValue));
        Raise(nameof(MetricTwoLabel));
        Raise(nameof(MetricTwoValue));
        Raise(nameof(SelectionLabel));
        Raise(nameof(SelectionDetail));
    }

    // ---- The strip and the status bar ---------------------------------------------------------

    private void RefreshVolumeStrip()
    {
        IReadOnlyList<StorageVolume> volumes;
        try
        {
            volumes = _volumeProvider.GetFixedVolumes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _volumeStripEntries.Clear();
            return;
        }

        string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);

        _volumeStripEntries.Clear();
        foreach (VolumeStripEntry entry in VolumeStrip.Build(volumes, systemRoot, new Dictionary<string, long>()))
        {
            _volumeStripEntries.Add(entry);
        }
    }

    private void UpdateStatusBar()
    {
        StatusBar.Facts.Clear();

        if (ViewModel.Volumes.Count == 0)
        {
            StatusBar.Facts.Add(new StatusFact("Reading volumes"));
            return;
        }

        int lowVolumes = ViewModel.Volumes.Count(row =>
            row.Volume.SizeBytes > 0 && 1 - row.Volume.UsedFraction < AttentionSignalBuilder.LowFreeFraction);

        StatusBar.Facts.Add(lowVolumes == 0
            ? new StatusFact($"{ViewModel.Volumes.Count} volumes healthy", StatusEmphasis.Good)
            : new StatusFact(
                lowVolumes == 1 ? "1 volume low on space" : $"{lowVolumes} volumes low on space",
                StatusEmphasis.Bad));

        StatusBar.Facts.Add(new StatusFact(LastScanText));

        if (Reclaim.HasScanned && Reclaim.FoundBytes > 0)
        {
            StatusBar.Facts.Add(new StatusFact(
                $"{ReclaimFormat.Bytes(Reclaim.FoundBytes)} reclaimable", StatusEmphasis.Warn));
        }
    }

    // ---- Layout ------------------------------------------------------------------------------

    private void TableCard_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        _cardWidth = args.NewSize.Width;

        double available = Math.Max(200, _cardWidth - SignalFixedColumnsWidth);
        if (Math.Abs(available - _signalColumnWidth) < 0.5)
        {
            return;
        }

        _signalColumnWidth = available;
        Raise(nameof(SignalColumnWidth));
    }

    // ---- Helpers -----------------------------------------------------------------------------

    /// <summary>
    /// How a volume is identified in the history file. Drive letter where there is one, because that
    /// is what the user sees; the label otherwise, which is the only other name a letterless volume
    /// has.
    /// </summary>
    private static string VolumeId(VolumeInfo volume) => volume.DriveLetter is char letter
        ? $"{letter}:"
        : volume.Label.Length > 0 ? volume.Label : "Unnamed volume";

    private static bool IsSystemRow(VolumeRowViewModel row)
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return row.Volume.DriveLetter is char letter
            && system.Length > 0
            && char.ToUpperInvariant(system[0]) == char.ToUpperInvariant(letter);
    }

    /// <summary>A duration as the largest unit that still reads as a whole number.</summary>
    private static string Span(TimeSpan span) => span.TotalDays >= 1
        ? $"{span.TotalDays:0} {(span.TotalDays < 2 ? "day" : "days")}"
        : span.TotalHours >= 1
            ? $"{span.TotalHours:0} {(span.TotalHours < 2 ? "hour" : "hours")}"
            : $"{Math.Max(1, span.TotalMinutes):0} minutes";

    private static string Ago(DateTimeOffset moment)
    {
        TimeSpan elapsed = DateTimeOffset.UtcNow - moment;
        return elapsed < TimeSpan.FromMinutes(1) ? "just now" : $"{Span(elapsed)} ago";
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
