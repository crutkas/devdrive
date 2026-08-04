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
    /// Everything in a cache row that is not the name, in DIPs: the WHERE, SIZE and ACTION columns
    /// (78 + 72 + 104), the three 12px gaps, the row's own 14px horizontal padding, and the card's
    /// 1px border on each side.
    /// </summary>
    private const double TableFixedColumnsWidth = 78 + 72 + 104 + (12 * 3) + 28 + 2;

    private readonly HashSet<PackageCacheRowViewModel> _hooked = new();
    private readonly IVolumeProvider _volumeProvider = new SystemVolumeProvider();

    private double _nameColumnWidth = 220;

    public PackageCachesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PackageCachesViewModel Caches => App.Shared.PackageCaches;

    /// <summary>The rail's bands, in rail order. Rebuilt in place so the selection survives a regroup.</summary>
    public ObservableCollection<CacheGroupViewModel> Groups { get; } =
    [
        new(CacheGroupKind.All, "All caches", "\uE8B7"),
        new(CacheGroupKind.NeedsAction, "Needs action", "\uE7BA"),
        new(CacheGroupKind.OnDevDrive, "On your Dev Drive", "\uE73E"),
        new(CacheGroupKind.NotInstalled, "Not installed", "\uE711"),
    ];

    /// <summary>The rows the centre table is showing — the selected band, needs-action first.</summary>
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
    /// An explicit width for the tool-name column, shared by the header and every row.
    /// <para>
    /// Same reasoning as the Reclaim and Space rooms: the fixed columns plus gaps and padding claim a
    /// known number of DIPs, so computing the remainder makes the floor explicit and keeps the header
    /// aligned with the rows for free, because both read this one number.
    /// </para>
    /// </summary>
    public GridLength NameColumnWidth => new(_nameColumnWidth);

    private CacheGroupViewModel? _selectedGroup;

    /// <summary>Which band the table is filtered to. Never null once the page has loaded.</summary>
    public CacheGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            // A ListView clears its selection while its ItemsSource is being rebuilt. Honouring that
            // would drop the user back to an unfiltered table every time a move completes.
            if (value is null || ReferenceEquals(value, _selectedGroup))
            {
                return;
            }

            _selectedGroup = value;
            Raise(nameof(SelectedGroup));
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

    /// <summary>The rail head's status: how much is still on the system drive, or that nothing is.</summary>
    public string StatusText
    {
        get
        {
            ulong onSystem = Sum(Caches.Caches.Where(IsStillOnSystemDrive));
            return onSystem == 0UL ? "all placed" : $"{ByteSizeFormatter.Format(onSystem)} on C:";
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
        _selectedGroup ??= Groups[0];
        Raise(nameof(SelectedGroup));
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

    /// <summary>Recomputes the rail's counts and totals, then the table, strip and status bar.</summary>
    private void Regroup()
    {
        // Without a Dev Drive there is nothing to move to, so the bands stop being about placement and
        // start being about detection. Same rows, honest labels.
        bool detectionOnly = !Caches.HasDevDrive;
        Group(CacheGroupKind.NeedsAction).Title = detectionOnly ? "Detected on this PC" : "Needs action";
        Group(CacheGroupKind.NotInstalled).Title = detectionOnly ? "Not detected" : "Not installed";

        foreach (CacheGroupViewModel group in Groups)
        {
            List<PackageCacheRowViewModel> rows = InBand(group.Kind).ToList();
            group.Set(rows.Count, Sum(rows));
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

        List<PackageCacheRowViewModel> rows = InBand(SelectedGroup?.Kind ?? CacheGroupKind.All).ToList();
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
        Raise(nameof(SelectedGroup));
    }

    /// <summary>
    /// The rows in a band, needs-action first. The status column already names each band, so a
    /// mixed "All" view reads as grouped without needing header rows inside the table.
    /// </summary>
    private IEnumerable<PackageCacheRowViewModel> InBand(CacheGroupKind kind) =>
        Caches.Caches.Where(row => Matches(row, kind)).OrderBy(BandOrder);

    private bool Matches(PackageCacheRowViewModel row, CacheGroupKind kind) => kind switch
    {
        CacheGroupKind.All => true,
        CacheGroupKind.NeedsAction => BandOrder(row) == 0,
        CacheGroupKind.OnDevDrive => BandOrder(row) == 1,
        _ => BandOrder(row) == 2,
    };

    /// <summary>0 needs action, 1 on the Dev Drive, 2 not installed.</summary>
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

    private CacheGroupViewModel Group(CacheGroupKind kind) => Groups.First(g => g.Kind == kind);

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
    /// a wider name column grows the header's desired width, the grid is arranged at that desired
    /// width, and the next measurement reports the inflated number.
    /// </summary>
    private void TableCard_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        double available = Math.Max(140, args.NewSize.Width - TableFixedColumnsWidth);
        if (Math.Abs(available - _nameColumnWidth) < 0.5)
        {
            return;
        }

        _nameColumnWidth = available;
        Raise(nameof(NameColumnWidth));
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
