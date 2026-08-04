using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using DevDriveManager.Controls;
using DevDriveManager.ViewModels;
using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

// Both DevDriveCore and DevDriveStorage ship a ByteSizeFormatter. The Core one takes the ulong the
// cache rows already carry; the Storage one takes a long. Aliased rather than qualified inline so the
// choice is stated once instead of at each call site.
using ByteSizeFormatter = DevDriveCore.ByteSizeFormatter;

namespace DevDriveManager.Pages;

/// <summary>
/// The Caches room: which developer tools keep a package cache, where each one lives, and what moving
/// it to the Dev Drive would change.
/// </summary>
/// <remarks>
/// <para>
/// The room owns nothing but presentation. Every cache row, the reversible move engine, and the
/// "Move all" flow live on the shared <see cref="PackageCachesViewModel"/>; this page groups the flat
/// <see cref="PackageCachesViewModel.Caches"/> collection into bands, filters the table to the
/// selected band, and keeps the volume strip and status bar in step.
/// </para>
/// <para>
/// Rows are ordered needs-action first and the room opens on "All caches" rather than on the
/// actionable band. The whole inventory is a dozen tools, so showing it costs nothing, and a room
/// that opens already filtered has to explain what it is hiding before the user can trust the counts.
/// </para>
/// </remarks>
public sealed partial class PackageCachesPage : Page, INotifyPropertyChanged
{
    /// <summary>
    /// Everything in a cache row that is not the location, in DIPs: the ECOSYSTEM, REDIRECTED BY,
    /// SIZE, STATUS and ACTION columns (164 + 170 + 88 + 96 + 104), the five 12px gaps, the row's own
    /// 14px horizontal padding, and the card's 1px border on each side.
    /// </summary>
    private const double TableFixedColumnsWidth = 164 + 170 + 88 + 96 + 104 + (12 * 5) + 28 + 2;

    private readonly HashSet<PackageCacheRowViewModel> _hooked = new();
    private readonly IVolumeProvider _volumeProvider = new SystemVolumeProvider();

    private double _locationColumnWidth = 220;

    public PackageCachesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PackageCachesViewModel Caches => App.Shared.PackageCaches;

    /// <summary>The table's two tabs, in head order. Rebuilt in place so the selection survives a regroup.</summary>
    public ObservableCollection<CacheTabViewModel> Tabs { get; } =
    [
        new(CacheTabKind.Detected, "Detected"),
        new(CacheTabKind.NotInstalled, "Not installed"),
    ];

    /// <summary>The rows the table is showing — the selected tab, needs-action first.</summary>
    public ObservableCollection<PackageCacheRowViewModel> VisibleCaches { get; } = new();

    private readonly ObservableCollection<VolumeStripEntry> _volumeStripEntries = new();

    /// <summary>
    /// Volumes for the context strip, with the still-on-C: cache bytes as their movable overlay.
    /// <para>
    /// Exposed as a read-only list on purpose. A mutable collection property on a page makes the XAML
    /// type generator treat the item type as XAML-constructible and emit setters for its members, which
    /// does not compile against <c>VolumeStripEntry</c>'s init-only properties. The Space room hit the
    /// same wall. The runtime instance is still an <see cref="ObservableCollection{T}"/>, so the strip
    /// keeps updating live.
    /// </para>
    /// </summary>
    public IReadOnlyList<VolumeStripEntry> VolumeStripEntries => _volumeStripEntries;

    /// <summary>
    /// An explicit width for the location column, shared by the header and every row.
    /// <para>
    /// Same reasoning as the Reclaim and Space rooms: the fixed columns plus gaps and padding claim a
    /// known number of DIPs, so computing the remainder makes the floor explicit and keeps the header
    /// aligned with the rows for free, because both read this one number.
    /// </para>
    /// </summary>
    public GridLength LocationColumnWidth => new(_locationColumnWidth);

    private CacheTabViewModel? _selectedTab;

    /// <summary>Which tab the table is filtered to. Never null once the page has loaded.</summary>
    public CacheTabViewModel? SelectedTab
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
            RefreshVisible();
        }
    }

    private PackageCacheRowViewModel? _selectedCache;

    /// <summary>The row the inspector is describing.</summary>
    public PackageCacheRowViewModel? SelectedCache
    {
        get => _selectedCache;
        set
        {
            if (ReferenceEquals(value, _selectedCache))
            {
                return;
            }

            _selectedCache = value;
            Raise(nameof(SelectedCache));
            RaiseInspector();
        }
    }

    /// <summary>
    /// The head chip: how many detected caches already sit on the Dev Drive. A fraction rather than a
    /// byte total because the question the chip answers is "am I done here", and bytes cannot answer
    /// that — a single large cache left behind reads as progress when counted in gigabytes.
    /// </summary>
    public string StatusText
    {
        get
        {
            if (!Caches.HasDevDrive)
            {
                int found = Caches.Caches.Count(row => BandOrder(row) != 2);
                return found == 1 ? "1 cache found" : $"{found} caches found";
            }

            List<PackageCacheRowViewModel> detected = InTab(CacheTabKind.Detected).ToList();
            if (detected.Count == 0)
            {
                return "nothing to place";
            }

            int placed = detected.Count(row => row.IsOnDevDrive);
            return placed == detected.Count
                ? $"all {detected.Count} already on {DevDriveLabel}"
                : $"{placed} of {detected.Count} already on {DevDriveLabel}";
        }
    }

    /// <summary>The Dev Drive's letter with its colon, or a generic word if we cannot name it.</summary>
    private string DevDriveLabel
    {
        get
        {
            string? root = _volumeStripEntries
                .FirstOrDefault(entry => !entry.IsSystemVolume)?.Volume.RootPath;
            return string.IsNullOrWhiteSpace(root) || root.Length < 2
                ? "your Dev Drive"
                : root[..2];
        }
    }

    /// <summary>
    /// What every inspector section says when the selected band is empty. A blank line under a
    /// section label reads as a bug; an em dash reads as "there is nothing here", which is the truth.
    /// </summary>
    private const string NothingSelected = "\u2014";

    /// <summary>Where the selected cache is today, or the guidance for one we could not find.</summary>
    public string InspectorLocation =>
        SelectedCache is null ? NothingSelected
        : SelectedCache.IsNotFound && !SelectedCache.IsMapped
            ? "We didn't find this tool's cache. If you use it, point us at the folder and we'll track it."
            : SelectedCache.ResolvedPath;

    /// <summary>Where moving would put it, or why there is nowhere to put it.</summary>
    public string InspectorDestination
    {
        get
        {
            if (SelectedCache is null)
            {
                return NothingSelected;
            }

            if (!Caches.HasDevDrive)
            {
                return "This PC has no Dev Drive yet, so there is nowhere to move caches to. Create one in the Create room.";
            }

            return SelectedCache.IsOnDevDrive
                ? "Already on your Dev Drive."
                : SelectedCache.CanMove
                    ? "Your Dev Drive, under the tool's own folder. The original stays until you confirm."
                    : "Nothing to move until this tool's cache exists.";
        }
    }

    /// <summary>What the move actually does to the machine — the "should I" the inspector exists for.</summary>
    public string InspectorEffect
    {
        get
        {
            if (SelectedCache is null)
            {
                return NothingSelected;
            }

            if (SelectedCache.IsOnDevDrive)
            {
                return "Nothing further. This cache already reads and writes on the Dev Drive, where "
                    + "antivirus runs in async mode.";
            }

            return "The cache is copied to your Dev Drive and the tool's per-user environment variable "
                + "is repointed at the copy. Nothing is deleted, and a receipt is kept so \u201CMove back\u201D "
                + "can undo it.";
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Caches.CachesChanged += OnCachesChanged;
        Caches.InventoryReset += OnInventoryReset;
        Caches.PropertyChanged += OnCachesPropertyChanged;
        _selectedTab ??= Tabs[0];
        _selectedTab.IsSelected = true;
        Raise(nameof(SelectedTab));
        HookRows();
        Regroup();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Caches.CachesChanged -= OnCachesChanged;
        Caches.InventoryReset -= OnInventoryReset;
        Caches.PropertyChanged -= OnCachesPropertyChanged;
        foreach (PackageCacheRowViewModel row in _hooked)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        _hooked.Clear();
    }

    private void OnCachesChanged(object? sender, System.EventArgs e)
    {
        HookRows();
        Regroup();
    }

    private void OnInventoryReset(object? sender, System.EventArgs e) => Regroup();

    private void OnCachesPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateStatusBar();

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A move's progress ticks (MoveProgressPercent and friends) must not churn the lists, so only
        // the properties that change which band a row belongs to — or how big it is — re-band.
        if (e.PropertyName is nameof(PackageCacheRowViewModel.CanMove)
            or nameof(PackageCacheRowViewModel.IsSet)
            or nameof(PackageCacheRowViewModel.IsMapped)
            or nameof(PackageCacheRowViewModel.IsMappedToDevDrive)
            or nameof(PackageCacheRowViewModel.SizeBytes))
        {
            Regroup();
            if (ReferenceEquals(sender, SelectedCache))
            {
                RaiseInspector();
            }
        }
    }

    /// <summary>Subscribe to every current row once; drop rows that are no longer present.</summary>
    private void HookRows()
    {
        foreach (PackageCacheRowViewModel row in _hooked.Where(r => !Caches.Caches.Contains(r)).ToList())
        {
            row.PropertyChanged -= OnRowPropertyChanged;
            _hooked.Remove(row);
        }

        foreach (PackageCacheRowViewModel row in Caches.Caches)
        {
            if (_hooked.Add(row))
            {
                row.PropertyChanged += OnRowPropertyChanged;
            }
        }
    }

    /// <summary>Recomputes the tab counts, then the table, strip and status bar.</summary>
    private void Regroup()
    {
        // Without a Dev Drive there is nothing to move to, so "not installed" stops being about a
        // missing tool and starts being about a cache we could not find. Same rows, honest labels.
        Tab(CacheTabKind.NotInstalled).Title = Caches.HasDevDrive ? "Not installed" : "Not detected";

        foreach (CacheTabViewModel tab in Tabs)
        {
            tab.Count = InTab(tab.Kind).Count();
        }

        RefreshVisible();
        RefreshVolumeStrip();
        Raise(nameof(StatusText));
        UpdateStatusBar();
    }

    /// <summary>
    /// Rebuilds the table in place rather than reassigning the collection, so the ListView keeps its
    /// scroll position and its selection across a move.
    /// </summary>
    private void RefreshVisible()
    {
        PackageCacheRowViewModel? wasSelected = SelectedCache;

        List<PackageCacheRowViewModel> rows = InTab(SelectedTab?.Kind ?? CacheTabKind.Detected).ToList();
        VisibleCaches.Clear();
        foreach (PackageCacheRowViewModel row in rows)
        {
            VisibleCaches.Add(row);
        }

        // Re-point the inspector rather than blanking it: a row that is still on screen after a
        // regroup is still the row the user was reading about. Falling back to the first row rather
        // than to null keeps the inspector answering something — unlike Reclaim, selection here is a
        // reading cursor, not a delete list, so a default costs nothing.
        SelectedCache = wasSelected is not null && rows.Contains(wasSelected)
            ? wasSelected
            : rows.FirstOrDefault();
    }

    /// <summary>
    /// The rows on a tab, needs-action first. The status column already names where each cache lives,
    /// so a mixed Detected view reads as grouped without needing header rows inside the table.
    /// </summary>
    private IEnumerable<PackageCacheRowViewModel> InTab(CacheTabKind kind) =>
        Caches.Caches.Where(row => Matches(row, kind)).OrderBy(BandOrder);

    /// <summary>
    /// Detected covers both placements — a cache we found is a cache we found, whichever volume it is
    /// on, and the Status column is what distinguishes them.
    /// </summary>
    private bool Matches(PackageCacheRowViewModel row, CacheTabKind kind) => kind switch
    {
        CacheTabKind.Detected => BandOrder(row) != 2,
        _ => BandOrder(row) == 2,
    };

    /// <summary>0 needs action, 1 on the Dev Drive, 2 not installed. Also the table's sort key.</summary>
    private int BandOrder(PackageCacheRowViewModel row)
    {
        if (!Caches.HasDevDrive)
        {
            return row.Info.Detected || row.IsSet || row.IsMapped ? 0 : 2;
        }

        return row.IsOnDevDrive ? 1
            : row.CanMove || row.IsMapped || row.Info.Detected ? 0
            : 2;
    }

    private CacheTabViewModel Tab(CacheTabKind kind) => Tabs.First(t => t.Kind == kind);

    private bool IsStillOnSystemDrive(PackageCacheRowViewModel row) =>
        Caches.HasDevDrive && !row.IsOnDevDrive && row.CanMove;

    private static ulong Sum(IEnumerable<PackageCacheRowViewModel> rows) =>
        rows.Aggregate(0UL, (total, row) => total + row.SizeBytes);

    /// <summary>
    /// The strip, with the bytes that would leave the system drive shown as its movable band — the
    /// same slot Reclaim uses for bytes that would be deleted. A move is not a delete, but from the
    /// volume's point of view the outcome is identical, which is what the bar is drawing.
    /// </summary>
    private void RefreshVolumeStrip()
    {
        IReadOnlyList<StorageVolume> volumes;
        try
        {
            volumes = _volumeProvider.GetFixedVolumes();
        }
        catch (IOException)
        {
            // A volume disappearing mid-session is a real event, not a bug. The room is still usable
            // without its strip, so this degrades rather than taking the page down.
            _volumeStripEntries.Clear();
            return;
        }
        catch (UnauthorizedAccessException)
        {
            _volumeStripEntries.Clear();
            return;
        }

        string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        Dictionary<string, long> movableByRoot = new(StringComparer.OrdinalIgnoreCase);
        foreach (PackageCacheRowViewModel row in Caches.Caches.Where(IsStillOnSystemDrive))
        {
            string? root = SafeRoot(row.ResolvedPath);
            if (root is null)
            {
                continue;
            }

            long bytes = (long)Math.Min(row.SizeBytes, long.MaxValue);
            movableByRoot[root] = movableByRoot.TryGetValue(root, out long existing) ? existing + bytes : bytes;
        }

        _volumeStripEntries.Clear();
        foreach (VolumeStripEntry entry in VolumeStrip.Build(volumes, systemRoot, movableByRoot))
        {
            _volumeStripEntries.Add(entry);
        }
    }

    /// <summary>
    /// A cache path can be anything the user typed into the map box, so this never throws: an
    /// unusable path contributes nothing to the strip rather than taking the room down.
    /// </summary>
    private static string? SafeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string? root = Path.GetPathRoot(path);
            return string.IsNullOrWhiteSpace(root) ? null : root;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void UpdateStatusBar()
    {
        StatusBar.Facts.Clear();

        if (Caches.IsCalculating)
        {
            StatusBar.Facts.Add(new StatusFact("Measuring caches"));
        }

        if (!Caches.HasDevDrive)
        {
            StatusBar.Facts.Add(new StatusFact("No Dev Drive on this PC", StatusEmphasis.Warn));
        }

        ulong onSystem = Sum(Caches.Caches.Where(IsStillOnSystemDrive));
        StatusBar.Facts.Add(onSystem > 0UL
            ? new StatusFact($"{ByteSizeFormatter.Format(onSystem)} still on C:", StatusEmphasis.Warn)
            : new StatusFact("Nothing left to move", StatusEmphasis.Good));

        ulong placed = Sum(Caches.Caches.Where(row => row.IsOnDevDrive));
        if (placed > 0UL)
        {
            StatusBar.Facts.Add(new StatusFact($"{ByteSizeFormatter.Format(placed)} on your Dev Drive", StatusEmphasis.Good));
        }
    }

    /// <summary>
    /// Measures the table card, not the header grid. Measuring the header would feed back on itself:
    /// a wider location column grows the header's desired width, the grid is arranged at that desired
    /// width, and the next measurement reports the inflated number.
    /// </summary>
    private void TableCard_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        double available = Math.Max(140, args.NewSize.Width - TableFixedColumnsWidth);
        if (Math.Abs(available - _locationColumnWidth) < 0.5)
        {
            return;
        }

        _locationColumnWidth = available;
        Raise(nameof(LocationColumnWidth));
    }

    /// <summary>
    /// Switches tabs. A Click handler rather than a bound command because the tab strip is chrome —
    /// the page owns which rows are visible, not the shared ViewModel, which several rooms share.
    /// </summary>
    private void SelectTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CacheTabViewModel tab })
        {
            SelectedTab = tab;
        }
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private void RaiseInspector()
    {
        Raise(nameof(InspectorLocation));
        Raise(nameof(InspectorDestination));
        Raise(nameof(InspectorEffect));
    }

    /// <summary>
    /// Lets the user pick a folder for a tool whose cache wasn't auto-detected, and writes the chosen path
    /// back onto the row's <see cref="PackageCacheRowViewModel.MapPath"/>. No mutation happens here — that
    /// is still gated behind the row's preview→confirm flow.
    /// </summary>
    private async void BrowseForMapPath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PackageCacheRowViewModel row)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            row.MapPath = folder.Path;
        }
    }

    /// <summary>
    /// Opens the bundled "what does moving a package cache do" explainer (<c>docs\PackageCacheMoves.md</c>,
    /// copied next to the app) in a scrollable, selectable read-only dialog.
    /// </summary>
    private async void LearnAboutCacheMoves_Click(object sender, RoutedEventArgs e)
    {
        string docPath = Path.Combine(AppContext.BaseDirectory, "docs", "PackageCacheMoves.md");
        string body;
        try
        {
            body = File.Exists(docPath)
                ? await File.ReadAllTextAsync(docPath)
                : "The package-cache explainer couldn't be found in this build. Moving a cache "
                    + "relocates it to your Dev Drive and repoints the per-user environment variable, "
                    + "leaving a reversible receipt so you can move it back at any time.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            body = "Couldn't open the package-cache explainer (" + ex.Message + "). Moving a cache "
                + "relocates it to your Dev Drive and repoints the per-user environment variable, leaving "
                + "a reversible receipt so you can move it back at any time.";
        }

        await ShowDocDialogAsync("What moving a package cache does", body, "LearnAboutCacheMovesDialog");
    }

    /// <summary>Shows a long-form, selectable read-only doc in a scrollable content dialog.</summary>
    private async System.Threading.Tasks.Task ShowDocDialogAsync(string title, string message, string automationId)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 480,
            },
            CloseButtonText = "Got it",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        ApplyDialogStyle(dialog);
        AutomationProperties.SetAutomationId(dialog, automationId);
        await dialog.ShowAsync();
    }

    private static void ApplyDialogStyle(ContentDialog dialog)
    {
        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) && style is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }
    }
}
