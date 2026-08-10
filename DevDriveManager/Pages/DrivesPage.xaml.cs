using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using DevDriveManager.Controls;
using DevDriveManager.ViewModels;
using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Pages;

/// <summary>
/// The Drives room: what this machine is partitioned into, and what sits in the Dev Drive's write
/// path.
/// </summary>
/// <remarks>
/// <para>
/// Two tabs over the same hardware. Volumes answers "what do I have, and is the Dev Drive set up
/// right"; Filter drivers answers "why is it faster", which the volumes table cannot answer because
/// the interesting fact there is about a filter that is <i>absent</i>.
/// </para>
/// <para>
/// The room owns nothing but presentation. Volumes and the elevation-gated trust reading both live on
/// the shared <see cref="App.Shared"/> view model, so this page reflects the same load as every other
/// room and never probes on its own.
/// </para>
/// </remarks>
public sealed partial class DrivesPage : Page, INotifyPropertyChanged
{
    /// <summary>
    /// Everything in a volume row that is not the notes column, in DIPs: VOLUME, FORMAT, CAPACITY,
    /// FREE and DEV DRIVE (188 + 78 + 100 + 92 + 104), the five 12px gaps, the row's own 14px
    /// horizontal padding, and the card's 1px border on each side.
    /// </summary>
    private const double VolumeFixedColumnsWidth = 188 + 78 + 100 + 92 + 104 + (12 * 5) + 28 + 2;

    /// <summary>
    /// The same arithmetic for the filter table: FILTER, ALTITUDE and ON-VOLUME (188 + 96 + 104),
    /// three gaps, the row padding and the card border.
    /// </summary>
    private const double FilterFixedColumnsWidth = 188 + 96 + 104 + (12 * 3) + 28 + 2;

    private readonly IVolumeProvider _volumeProvider = new SystemVolumeProvider();
    private readonly ObservableCollection<VolumeStripEntry> _volumeStripEntries = new();
    private readonly ObservableCollection<IoPathNodeViewModel> _ioPathLeft = new();
    private readonly ObservableCollection<IoPathNodeViewModel> _ioPathRight = new();

    private double _cardWidth;
    private double _notesColumnWidth = 260;

    public DrivesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainPageViewModel ViewModel => App.Shared;

    /// <summary>The two views of this machine's storage, in head order.</summary>
    public ObservableCollection<DriveTabViewModel> Tabs { get; } =
    [
        new(DriveTabKind.Volumes, "Volumes"),
        new(DriveTabKind.FilterDrivers, "Filter drivers"),
    ];

    /// <summary>
    /// The strip above the room, same control and same meaning as every other room: what each volume
    /// looks like right now. Nothing here moves bytes, so no volume carries a movable band.
    /// </summary>
    public IReadOnlyList<VolumeStripEntry> VolumeStripEntries => _volumeStripEntries;

    /// <summary>
    /// An explicit width for whichever column absorbs the slack, shared by the header and every row.
    /// Both tables read this one number, which is what keeps their headers aligned with their rows.
    /// </summary>
    public GridLength NotesColumnWidth => new(_notesColumnWidth);

    private DriveTabViewModel? _selectedTab;

    /// <summary>Which view the centre column is showing. Never null once the page has loaded.</summary>
    public DriveTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedTab))
            {
                return;
            }

            if (_selectedTab is not null)
            {
                _selectedTab.IsSelected = false;
            }

            _selectedTab = value;
            _selectedTab.IsSelected = true;
            Raise(nameof(SelectedTab));
            Raise(nameof(ShowVolumes));
            Raise(nameof(ShowFilters));
            Raise(nameof(ShowFilterEmptyState));
            Raise(nameof(StatusText));
            UpdateColumnWidth();
        }
    }

    public bool ShowVolumes => SelectedTab?.Kind != DriveTabKind.FilterDrivers;

    public bool ShowFilters => SelectedTab?.Kind == DriveTabKind.FilterDrivers
        && ViewModel.Trust.HasFilters;

    /// <summary>
    /// The filters tab with nothing to list. Kept to one muted line, because the explanation and the
    /// button that fixes it live in the I/O path card below — saying it twice would read as two
    /// separate problems.
    /// </summary>
    public bool ShowFilterEmptyState => SelectedTab?.Kind == DriveTabKind.FilterDrivers
        && !ViewModel.Trust.HasFilters;

    // ---- The I/O path stack ----------------------------------------------------------------------

    /// <summary>
    /// The first half of the path, top to bottom. Two columns rather than one, because a stack of ten
    /// nodes in a single column is taller than the card and turns the room's one diagram into a scroll.
    /// </summary>
    public IReadOnlyList<IoPathNodeViewModel> IoPathLeft => _ioPathLeft;

    public IReadOnlyList<IoPathNodeViewModel> IoPathRight => _ioPathRight;

    /// <summary>True once an elevated read has told us which filters are actually attached.</summary>
    public bool HasIoPath => ViewModel.Trust.HasFilters;

    /// <summary>The inverse — the card shows what it would show, and the button that gets us there.</summary>
    public bool ShowIoPathEmptyState => !ViewModel.Trust.HasFilters;

    private VolumeRowViewModel? _selectedVolume;

    private bool _refreshQueued;

    private bool _volumesDirty;

    /// <summary>The row the inspector describes. Follows the table's selection.</summary>
    public VolumeRowViewModel? SelectedVolume
    {
        get => _selectedVolume;
        set
        {
            // A ListView clears its selection while its ItemsSource is being rebuilt; honouring that
            // would blank the inspector every time a rescan completes. ResolveSelectedVolume re-points
            // the selection at a live row once the rebuild settles.
            if (value is null || ReferenceEquals(value, _selectedVolume))
            {
                return;
            }

            _selectedVolume = value;
            RaiseInspector();
        }
    }

    /// <summary>
    /// The head chip. On Volumes it answers "is this machine set up"; on Filter drivers it answers
    /// the only question that tab exists for — how many filters actually run.
    /// </summary>
    public string StatusText
    {
        get
        {
            if (SelectedTab?.Kind == DriveTabKind.FilterDrivers)
            {
                return ViewModel.Trust.HasFilters
                    ? ViewModel.Trust.FilterSummaryText
                    : "needs administrator";
            }

            int devDrives = ViewModel.Volumes.Count(row => row.IsDevDrive);
            return devDrives == 0
                ? "no Dev Drive yet"
                : $"{devDrives} of {ViewModel.Volumes.Count} is a Dev Drive";
        }
    }

    // ---- Inspector -------------------------------------------------------------------------------

    public string InspectorTitle => SelectedVolume?.Header ?? "Nothing selected";

    public string InspectorWhatThisIs => SelectedVolume is null
        ? "Select a volume."
        : SelectedVolume.NotesText.Length > 0
            ? SelectedVolume.NotesText
            : $"A {SelectedVolume.FileSystemType} volume with no special role on this PC.";

    public string InspectorCapacity => SelectedVolume is null
        ? string.Empty
        : $"{SelectedVolume.FreeText} free of {SelectedVolume.CapacityText}";

    /// <summary>
    /// What the selected volume's format means for a build. The format column states the name; this
    /// states the consequence, which is the reason anyone reads that column.
    /// </summary>
    public string InspectorFormat
    {
        get
        {
            if (SelectedVolume is null)
            {
                return string.Empty;
            }

            if (SelectedVolume.IsDevDrive)
            {
                return SelectedVolume.IsTrusted
                    ? $"{SelectedVolume.FileSystemType} on a trusted Dev Drive. Defender scans asynchronously here, "
                        + "which is where the build speed comes from \u2014 and the reason to keep only source and "
                        + "caches on it."
                    : $"{SelectedVolume.FileSystemType}, formatted as a Dev Drive but not currently trusted. "
                        + "Without trust it is scanned like any other volume.";
            }

            return $"{SelectedVolume.FileSystemType}. Every write is scanned synchronously, the same as any "
                + "ordinary volume.";
        }
    }

    /// <summary>Free space on the Dev Drive — is there room for what you were about to put there.</summary>
    public string DevDriveFreeText =>
        ViewModel.Volumes.FirstOrDefault(row => row.IsDevDrive)?.FreeText ?? "\u2014";

    /// <summary>Free space on the system volume — what a new Dev Drive would have to come out of.</summary>
    public string SystemFreeText =>
        ViewModel.Volumes.FirstOrDefault(IsSystemRow)?.FreeText ?? "\u2014";

    // ---- Lifecycle -------------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Volumes.CollectionChanged += OnCollectionChanged;
        ViewModel.Trust.Filters.CollectionChanged += OnCollectionChanged;
        ViewModel.Trust.PropertyChanged += OnTrustChanged;
        AttachItemsSources();

        _selectedTab ??= Tabs[0];
        _selectedTab.IsSelected = true;
        Raise(nameof(SelectedTab));
        Raise(nameof(ShowVolumes));
        Raise(nameof(ShowFilters));
        Raise(nameof(ShowFilterEmptyState));

        _volumesDirty = true;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Volumes.CollectionChanged -= OnCollectionChanged;
        ViewModel.Trust.Filters.CollectionChanged -= OnCollectionChanged;
        ViewModel.Trust.PropertyChanged -= OnTrustChanged;
        DetachItemsSources();
    }

    /// <summary>
    /// Drops the two lists this page pointed at collections on the shared ViewModel.
    /// </summary>
    /// <remarks>
    /// The same rule the three handlers above already follow, applied to the subscriptions XAML
    /// makes on this page's behalf. An <c>ItemsSource</c> binding to a collection on
    /// <see cref="App.Shared"/> registers a <em>native</em> listener that outlives the page, and
    /// NavigationCacheMode is left at its Disabled default, so each visit left the previous list
    /// subscribed. Dispatching a collection change to a torn-down control fail-fasts the process
    /// from native code, which nothing here can catch; <c>SpacePage</c> carries the crash dumps
    /// that proved it. Measured on a live app: two handlers on <c>Volumes</c> with the room
    /// unloaded, where a page that releases cleanly leaves none.
    /// </remarks>
    private void DetachItemsSources()
    {
        VolumesList.ItemsSource = null;
        FiltersList.ItemsSource = null;
    }

    /// <summary>
    /// Puts back what <see cref="DetachItemsSources"/> dropped. Safe to repeat because both
    /// bindings are <c>x:Bind</c>'s implicit OneTime, so nothing re-pushes a source behind us.
    /// </summary>
    private void AttachItemsSources()
    {
        VolumesList.ItemsSource = ViewModel.Volumes;
        FiltersList.ItemsSource = ViewModel.Trust.Filters;
    }

    private void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        RequestRefresh(volumesChanged: ReferenceEquals(sender, ViewModel.Volumes));

    /// <summary>
    /// Only the trust facts this room actually shows are worth a refresh. <see cref="TrustFiltersViewModel"/>
    /// assigns a dozen properties per update and each one raises here.
    /// </summary>
    private void OnTrustChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TrustFiltersViewModel.HasFilters):
            case nameof(TrustFiltersViewModel.FilterSummaryText):
            case nameof(TrustFiltersViewModel.ShowSeeFilters):
            case nameof(TrustFiltersViewModel.CouldNotReadFilters):
            case nameof(TrustFiltersViewModel.CouldNotReadFiltersNote):
                RequestRefresh(volumesChanged: false);
                break;
        }
    }

    /// <summary>
    /// Coalesces refreshes onto one dispatcher tick.
    /// </summary>
    /// <remarks>
    /// A reload clears and refills two observable collections and assigns a dozen properties, so a
    /// direct <see cref="Refresh"/> per notification runs it ~20 times for one logical update. That
    /// matters because <see cref="RefreshVolumeStrip"/> re-enumerates every fixed volume — a
    /// CreateFile plus a DeviceIoControl per volume, synchronously, on the UI thread — and rebuilds
    /// every card in the strip. One tick's worth of notifications is one logical change.
    /// </remarks>
    private void RequestRefresh(bool volumesChanged)
    {
        _volumesDirty |= volumesChanged;

        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _refreshQueued = false;
            Refresh();
        }))
        {
            // No dispatcher to defer onto (the page is being torn down) — do it inline rather than
            // silently dropping the update.
            _refreshQueued = false;
            Refresh();
        }
    }

    /// <summary>Recomputes the tab counts, the strip, the head chip and the status bar.</summary>
    private void Refresh()
    {
        Tab(DriveTabKind.Volumes).Count = ViewModel.Volumes.Count;
        Tab(DriveTabKind.FilterDrivers).Count = ViewModel.Trust.Filters.Count;

        ResolveSelectedVolume();

        // The strip costs a full volume enumeration, so it only re-runs when the volume set moved.
        if (_volumesDirty)
        {
            _volumesDirty = false;
            RefreshVolumeStrip();
        }

        BuildIoPath();

        RaiseInspector();
        Raise(nameof(ShowFilters));
        Raise(nameof(ShowFilterEmptyState));
        Raise(nameof(HasIoPath));
        Raise(nameof(ShowIoPathEmptyState));
        Raise(nameof(DevDriveFreeText));
        Raise(nameof(SystemFreeText));
        Raise(nameof(StatusText));
        UpdateStatusBar();
    }

    /// <summary>
    /// Re-points the selection at a row that is actually in the list.
    /// </summary>
    /// <remarks>
    /// A reload replaces every row object, so keeping the old reference would leave the ListView with
    /// a SelectedItem that is not in its ItemsSource: no row highlighted, while the inspector still
    /// reads out the previous scan's numbers. Match the same volume so the user's choice survives a
    /// rescan, and fall back to the Dev Drive — the volume the room is about, and on a machine with
    /// one the only row where every column says something the user did not already know.
    /// </remarks>
    private void ResolveSelectedVolume()
    {
        // Match on AutomationId, not drive letter: a volume without a letter (Recovery) has a null
        // letter, and every such row would collapse onto the same match.
        string? keep = _selectedVolume?.AutomationId;

        VolumeRowViewModel? resolved =
            (keep is null ? null : ViewModel.Volumes.FirstOrDefault(row => row.AutomationId == keep))
            ?? ViewModel.Volumes.FirstOrDefault(row => row.IsDevDrive)
            ?? ViewModel.Volumes.FirstOrDefault();

        if (ReferenceEquals(resolved, _selectedVolume))
        {
            return;
        }

        _selectedVolume = resolved;
        Raise(nameof(SelectedVolume));
    }

    /// <summary>
    /// Lays the filters out as the path a write actually takes: altitude order, top to bottom, with
    /// the tool that issues the write at the head and the volume at the foot.
    /// </summary>
    /// <remarks>
    /// The table above says the same facts; this says what they mean. A build tool's write descends
    /// through every attached filter before it lands, and the reason a Dev Drive is fast is visible
    /// here as gaps in that descent — which a table of six rows cannot show.
    /// </remarks>
    private void BuildIoPath()
    {
        _ioPathLeft.Clear();
        _ioPathRight.Clear();

        if (ViewModel.Trust.Filters.Count == 0)
        {
            return;
        }

        VolumeRowViewModel? dev = ViewModel.Volumes.FirstOrDefault(row => row.IsDevDrive);
        string landing = dev is null
            ? "The volume"
            : $"{dev.FileSystemType} on {dev.DriveLetterText}";

        List<IoPathNodeViewModel> nodes =
        [
            new("Your build tools", "write()", "end", false),
        ];

        foreach (FilterRowViewModel filter in ViewModel.Trust.Filters)
        {
            nodes.Add(new IoPathNodeViewModel(filter.Name, filter.StackCaption, filter.IsAttached ? "on" : "off", true));
        }

        nodes.Add(new IoPathNodeViewModel(landing, ViewModel.Trust.FilterSummaryText, "end", true));

        // Split so the left column is the taller one on an odd count: the path reads down the left and
        // continues down the right, so a longer left column is the one that looks deliberate.
        int split = (nodes.Count + 1) / 2;
        for (int i = 0; i < nodes.Count; i++)
        {
            // The first node in each column has nothing above it to connect to.
            IoPathNodeViewModel node = nodes[i] with { ShowArrow = i != 0 && i != split };
            if (i < split)
            {
                _ioPathLeft.Add(node);
            }
            else
            {
                _ioPathRight.Add(node);
            }
        }
    }

    private void RaiseInspector()
    {
        Raise(nameof(SelectedVolume));
        Raise(nameof(InspectorTitle));
        Raise(nameof(InspectorWhatThisIs));
        Raise(nameof(InspectorCapacity));
        Raise(nameof(InspectorFormat));
    }

    /// <summary>
    /// The strip, with no movable band on any volume: this room reads the machine and changes nothing,
    /// so drawing bytes as "would leave" would be a claim it has no basis for.
    /// </summary>
    private void RefreshVolumeStrip()
    {
        IReadOnlyList<StorageVolume> volumes;
        try
        {
            volumes = _volumeProvider.GetFixedVolumes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A volume disappearing mid-session is a real event, not a bug. The room is still usable
            // without its strip, so this degrades rather than taking the page down.
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

        StatusBar.Facts.Add(new StatusFact(
            ViewModel.Volumes.Count == 1 ? "1 volume" : $"{ViewModel.Volumes.Count} volumes"));

        VolumeRowViewModel? dev = ViewModel.Volumes.FirstOrDefault(row => row.IsDevDrive);
        StatusBar.Facts.Add(dev is null
            ? new StatusFact("No Dev Drive on this PC", StatusEmphasis.Warn)
            : new StatusFact($"{dev.FreeText} free on {dev.DriveLetterText}", StatusEmphasis.Good));

        if (ViewModel.Trust.HasFilters)
        {
            StatusBar.Facts.Add(new StatusFact(ViewModel.Trust.FilterSummaryText));
        }
        else if (ViewModel.Trust.ShowSeeFilters)
        {
            StatusBar.Facts.Add(new StatusFact("Filter drivers need administrator"));
        }
    }

    private DriveTabViewModel Tab(DriveTabKind kind) => Tabs.First(t => t.Kind == kind);

    private static bool IsSystemRow(VolumeRowViewModel row)
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return row.Volume.DriveLetter is char letter
            && system.Length > 0
            && char.ToUpperInvariant(system[0]) == char.ToUpperInvariant(letter);
    }

    // ---- Layout and input ------------------------------------------------------------------------

    /// <summary>
    /// Measures the table card, not the header grid. Measuring the header would feed back on itself:
    /// a wider notes column grows the header's desired width, the grid is arranged at that desired
    /// width, and the next measurement reports the inflated number.
    /// </summary>
    private void TableCard_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        _cardWidth = args.NewSize.Width;
        UpdateColumnWidth();
    }

    /// <summary>
    /// The slack column, recomputed on a resize <i>and</i> on a tab switch — the two tables have
    /// different fixed columns, so the same card width leaves them different amounts of room.
    /// </summary>
    private void UpdateColumnWidth()
    {
        double fixedWidth = SelectedTab?.Kind == DriveTabKind.FilterDrivers
            ? FilterFixedColumnsWidth
            : VolumeFixedColumnsWidth;

        double available = Math.Max(140, _cardWidth - fixedWidth);
        if (Math.Abs(available - _notesColumnWidth) < 0.5)
        {
            return;
        }

        _notesColumnWidth = available;
        Raise(nameof(NotesColumnWidth));
    }

    /// <summary>
    /// Switches tabs. A Click handler rather than a bound command because the tab strip is chrome —
    /// the page owns which table is visible, not the shared ViewModel, which every room shares.
    /// </summary>
    private void SelectTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DriveTabViewModel tab })
        {
            SelectedTab = tab;
        }
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

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
    private void Volumes_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not null && args.Item is VolumeRowViewModel row)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(args.ItemContainer, row.AutomationId);
        }
    }
}
