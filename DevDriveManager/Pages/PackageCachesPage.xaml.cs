using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using DevDriveManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Pages;

/// <summary>
/// Shows developer-tool caches, their locations, and reversible move actions.
/// </summary>
/// <remarks>
/// The shared <see cref="PackageCachesViewModel"/> owns detection and moves. This page filters its
/// rows into tabs and presents the existing confirmation gates as native dialogs.
/// </remarks>
public sealed partial class PackageCachesPage : Page, INotifyPropertyChanged
{
    /// <summary>
    /// Everything in a cache row that is not the location, in DIPs: the ECOSYSTEM, REDIRECTED BY,
    /// SIZE, STATUS and ACTION columns (164 + 170 + 88 + 96 + 104), the five 12px gaps, the row's own
    /// 16px horizontal padding, and the card's 1px border on each side.
    /// </summary>
    private const double TableFixedColumnsWidth = 164 + 170 + 88 + 96 + 104 + (12 * 5) + 32 + 2;

    private readonly HashSet<PackageCacheRowViewModel> _hooked = new();

    private double _locationColumnWidth = 220;

    private bool _dialogOpen;
    private ContentDialog? _activeDialog;
    private ContentDialog? _moveAllDialog;
    private PackageCacheRowViewModel? _pendingRowConfirmation;

    public PackageCachesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PackageCachesViewModel Caches => App.Shared.PackageCaches;

    /// <summary>The native selector's two views, in display order.</summary>
    public ObservableCollection<CacheTabViewModel> Tabs { get; } =
    [
        new(CacheTabKind.Detected, "Detected"),
        new(CacheTabKind.NotInstalled, "Not installed"),
    ];

    public CacheTabViewModel DetectedTab => Tabs[0];

    public CacheTabViewModel NotInstalledTab => Tabs[1];

    /// <summary>The rows the table is showing — the selected tab, needs-action first.</summary>
    public ObservableCollection<PackageCacheRowViewModel> VisibleCaches { get; } = new();

    public bool ShowEmptyState => VisibleCaches.Count == 0;

    public string EmptyStateText => SelectedTab?.Kind == CacheTabKind.NotInstalled
        ? "All supported tool caches were found on this PC."
        : "No package caches detected on this PC.";

    public InfoBarSeverity MoveAllSeverity =>
        Caches.MoveAllHasFailures ? InfoBarSeverity.Error : InfoBarSeverity.Success;

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

    /// <summary>Which selector item filters the shared list. Never null once the page has loaded.</summary>
    public CacheTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedTab))
            {
                return;
            }

            _selectedTab = value;
            Raise(nameof(SelectedTab));
            RefreshVisible();
        }
    }

    /// <summary>
    /// How many detected caches already sit on the Dev Drive. A fraction rather than a byte total
    /// answers "am I done here" without hiding a single large cache left behind.
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
            char? letter = App.Shared.Volumes
                .FirstOrDefault(row => row.IsDevDrive && row.Volume.DriveLetter.HasValue)?.Volume.DriveLetter;
            return letter is { } driveLetter ? $"{driveLetter}:" : "your Dev Drive";
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Caches.CachesChanged += OnCachesChanged;
        Caches.InventoryReset += OnInventoryReset;
        Caches.PropertyChanged += OnCachesPropertyChanged;
        _selectedTab ??= CacheSelector.SelectedItem == NotInstalledSelector ? NotInstalledTab : DetectedTab;
        CacheSelector.SelectedItem ??= DetectedSelector;
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
        _activeDialog?.Hide();
        if (Caches.IsConfirmingMoveAll)
        {
            Caches.CancelMoveAllCommand.Execute(null);
        }

        if (_pendingRowConfirmation?.IsConfirmingMove == true)
        {
            _pendingRowConfirmation.CancelMoveCommand.Execute(null);
        }
    }

    private void OnCachesChanged(object? sender, System.EventArgs e)
    {
        HookRows();
        Regroup();
    }

    private void OnInventoryReset(object? sender, System.EventArgs e) => Regroup();

    private void OnCachesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageCachesViewModel.IsConfirmingMoveAll)
            && !Caches.IsConfirmingMoveAll)
        {
            _moveAllDialog?.Hide();
        }

        if (e.PropertyName == nameof(PackageCachesViewModel.MoveAllHasFailures))
        {
            Raise(nameof(MoveAllSeverity));
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageCacheRowViewModel.IsConfirmingMove)
            && ReferenceEquals(sender, _pendingRowConfirmation)
            && _pendingRowConfirmation?.IsConfirmingMove == false)
        {
            _activeDialog?.Hide();
        }

        // A move's progress ticks (MoveProgressPercent and friends) must not churn the lists, so only
        // the properties that change which band a row belongs to — or how big it is — re-band.
        if (e.PropertyName is nameof(PackageCacheRowViewModel.CanMove)
            or nameof(PackageCacheRowViewModel.IsSet)
            or nameof(PackageCacheRowViewModel.IsMapped)
            or nameof(PackageCacheRowViewModel.IsMappedToDevDrive)
            or nameof(PackageCacheRowViewModel.SizeBytes))
        {
            Regroup();
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

    /// <summary>Recomputes the tab counts, then the visible rows and status.</summary>
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
        Raise(nameof(StatusText));
    }

    /// <summary>Rebuilds the selected tab without replacing the collection bound to the list.</summary>
    private void RefreshVisible()
    {
        List<PackageCacheRowViewModel> rows = InTab(SelectedTab?.Kind ?? CacheTabKind.Detected).ToList();
        VisibleCaches.Clear();
        foreach (PackageCacheRowViewModel row in rows)
        {
            VisibleCaches.Add(row);
        }

        Raise(nameof(ShowEmptyState));
        Raise(nameof(EmptyStateText));
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

    /// <summary>
    /// Measures the table card, not the header grid. Measuring the header would feed back on itself:
    /// a wider location column grows the header's desired width, the grid is arranged at that desired
    /// width, and the next measurement reports the inflated number.
    /// </summary>
    private void TableCard_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        double available = Math.Max(180, args.NewSize.Width - TableFixedColumnsWidth);
        if (Math.Abs(available - _locationColumnWidth) < 0.5)
        {
            return;
        }

        _locationColumnWidth = available;
        Raise(nameof(LocationColumnWidth));
    }

    /// <summary>
    /// Let the native selector own focus, selection, and visual states; only filter the shared rows.
    /// </summary>
    private void CacheSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem == DetectedSelector)
        {
            SelectedTab = DetectedTab;
        }
        else if (sender.SelectedItem == NotInstalledSelector)
        {
            SelectedTab = NotInstalledTab;
        }
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private async void MoveAll_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || !IsLoaded || XamlRoot is null || !Caches.MoveAllCommand.CanExecute(null))
        {
            return;
        }

        try
        {
            Caches.MoveAllCommand.Execute(null);
            if (!Caches.IsConfirmingMoveAll)
            {
                return;
            }

            bool confirmed = await ShowConfirmationDialogAsync(
                "Move all package caches?",
                Caches.MoveAllConfirmBodyText,
                "Move all",
                "MoveAllConfirmDialog",
                isMoveAll: true);
            if (confirmed && IsLoaded && Caches.ConfirmMoveAllCommand.CanExecute(null))
            {
                await Caches.ConfirmMoveAllCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            ReportActionError("Move all", ex);
        }
        finally
        {
            if (Caches.IsConfirmingMoveAll)
            {
                Caches.CancelMoveAllCommand.Execute(null);
            }
        }
    }

    private async void MoveCache_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PackageCacheRowViewModel row })
        {
            await RequestRowConfirmationAsync(row, row.MoveCommand);
        }
    }

    private async void MapPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PackageCacheRowViewModel row })
        {
            await RequestRowConfirmationAsync(row, row.MapPathActionCommand);
        }
    }

    private async void MoveAndRemap_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PackageCacheRowViewModel row })
        {
            await RequestRowConfirmationAsync(row, row.MoveAndRemapCommand);
        }
    }

    private async System.Threading.Tasks.Task RequestRowConfirmationAsync(
        PackageCacheRowViewModel row,
        ICommand command)
    {
        if (_dialogOpen || !IsLoaded || XamlRoot is null
            || !Caches.Caches.Contains(row) || !command.CanExecute(null))
        {
            return;
        }

        try
        {
            command.Execute(null);
            if (!row.IsConfirmingMove)
            {
                return;
            }

            _pendingRowConfirmation = row;
            string title = row.PendingAction switch
            {
                PendingCacheAction.Map => $"Map {row.Header} cache?",
                PendingCacheAction.Remap => $"Move and remap {row.Header} cache?",
                _ => $"Move {row.Header} cache?",
            };
            string action = row.PendingAction switch
            {
                PendingCacheAction.Map => "Map path",
                PendingCacheAction.Remap => "Move & remap",
                _ => "Move",
            };

            bool confirmed = await ShowConfirmationDialogAsync(
                title, row.MoveConfirmBodyText, action, row.MoveConfirmDialogAutomationId);
            if (confirmed && IsLoaded && Caches.Caches.Contains(row)
                && row.IsConfirmingMove && row.ConfirmMoveCommand.CanExecute(null))
            {
                row.ConfirmMoveCommand.Execute(null);
            }
        }
        catch (Exception ex)
        {
            ReportActionError($"Updating {row.Header} cache", ex);
        }
        finally
        {
            if (row.IsConfirmingMove)
            {
                row.CancelMoveCommand.Execute(null);
            }

            _pendingRowConfirmation = null;
        }
    }

    private async System.Threading.Tasks.Task<bool> ShowConfirmationDialogAsync(
        string title, string body, string action, string automationId, bool isMoveAll = false)
    {
        if (_dialogOpen || !IsLoaded || XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = body,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        ApplyDialogStyle(dialog);
        AutomationProperties.SetAutomationId(dialog, automationId);

        _dialogOpen = true;
        _activeDialog = dialog;
        if (isMoveAll)
        {
            _moveAllDialog = dialog;
        }

        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _dialogOpen = false;
            _activeDialog = null;
            if (isMoveAll)
            {
                _moveAllDialog = null;
            }
        }
    }

    private void ReportActionError(string action, Exception exception)
    {
        System.Diagnostics.Debug.WriteLine($"[caches] {action} failed: {exception}");
        CachesActionError.Title = $"{action} failed";
        CachesActionError.Message = exception.Message;
        CachesActionError.Visibility = Visibility.Visible;
        CachesActionError.IsOpen = true;
    }

    private void CachesActionError_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        sender.Visibility = Visibility.Collapsed;

    private bool _pickerOpen;

    /// <summary>
    /// Lets the user pick a folder for a tool whose cache wasn't auto-detected, and writes the chosen path
    /// back onto the row's <see cref="PackageCacheRowViewModel.MapPath"/>. No mutation happens here — that
    /// is still gated behind the row's preview→confirm flow.
    /// </summary>
    /// <remarks>
    /// Guarded like every other picker and dialog in the app: a second picker opened over a live one
    /// throws, and this is an <c>async void</c> handler, so that throw would take the process with it.
    /// One Browse button exists per undetected row, which makes a stray double-click likely.
    /// </remarks>
    private async void BrowseForMapPath_Click(object sender, RoutedEventArgs e)
    {
        if (_pickerOpen || (sender as FrameworkElement)?.DataContext is not PackageCacheRowViewModel row)
        {
            return;
        }

        _pickerOpen = true;

        try
        {
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
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[caches] folder picker failed: {exception}");
        }
        finally
        {
            _pickerOpen = false;
        }
    }

    /// <summary>
    /// Opens the bundled "what does moving a package cache do" explainer (<c>docs\PackageCacheMoves.md</c>,
    /// copied next to the app) in a scrollable, selectable read-only dialog.
    /// </summary>
    private async void LearnAboutCacheMoves_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || !IsLoaded)
        {
            return;
        }

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

        try
        {
            await ShowDocDialogAsync("What moving a package cache does", body, "LearnAboutCacheMovesDialog");
        }
        catch (Exception ex)
        {
            ReportActionError("Opening cache guide", ex);
        }
    }

    /// <summary>Shows a long-form, selectable read-only doc in a scrollable content dialog.</summary>
    /// <remarks>
    /// Guarded because WinUI allows exactly one <see cref="ContentDialog"/> at a time and a second
    /// <c>ShowAsync</c> throws. A double-click on the button is the obvious way to reach that.
    /// </remarks>
    private async System.Threading.Tasks.Task ShowDocDialogAsync(string title, string message, string automationId)
    {
        if (_dialogOpen || !IsLoaded || XamlRoot is null)
        {
            return;
        }

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

        _dialogOpen = true;
        _activeDialog = dialog;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
            _activeDialog = null;
        }
    }

    private static void ApplyDialogStyle(ContentDialog dialog)
    {
        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) && style is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }
    }

    /// <summary>
    /// Forwards the row's AutomationId onto the generated <c>ListViewItem</c>, so recycled rows
    /// remain discoverable by assistive technology and UI automation.
    /// </summary>
    private void Caches_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not null && args.Item is PackageCacheRowViewModel row)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(args.ItemContainer, row.RowAutomationId);
        }
    }
}
