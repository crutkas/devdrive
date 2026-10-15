using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DevDriveCore;
using DevDriveManager.Services;
using DevDriveManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Pages;

/// <summary>
/// The Dev Drive room: the Dev Drive itself (or an offer to create one), whether it runs in
/// performance mode, the way into the deeper pages, and — beside it — the package caches that
/// belong on it, with the moves that put them there.
/// </summary>
/// <remarks>
/// Like Home, this page owns no data. It reads the shared view model, so what it shows always agrees
/// with the pages it links to. The cache panel only sorts the shared rows into two page-owned lists
/// and presents the view model's confirmation gates as native dialogs.
/// </remarks>
public sealed partial class DevDrivePage : Page, INotifyPropertyChanged
{
    private const string PerformanceModePrefix = "Performance mode: ";

    private readonly ObservableCollection<DriveCardViewModel> _drives = [];
    private readonly ObservableCollection<PackageCacheRowViewModel> _toMove = [];
    private readonly ObservableCollection<PackageCacheRowViewModel> _onDevDrive = [];
    private readonly HashSet<PackageCacheRowViewModel> _hooked = [];

    private bool _refreshQueued;
    private bool _dialogOpen;
    private bool _moving;
    private ContentDialog? _activeDialog;
    private ContentDialog? _moveAllDialog;

    public DevDrivePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainPageViewModel ViewModel => App.Shared;

    public PackageCachesViewModel Caches => App.Shared.PackageCaches;

    public IReadOnlyList<DriveCardViewModel> DriveCards => _drives;

    public int DriveColumns => Math.Max(1, _drives.Count);

    /// <summary>"On (async)", "Off (sync)", and so on, without the "Performance mode:" lead-in.</summary>
    public string PerformanceModeState { get; private set; } = string.Empty;

    /// <summary>A check for a cache already on the Dev Drive, a package for one that is not.</summary>
    public static string CacheGlyph(bool onDevDrive) => onDevDrive ? "\uE73E" : "\uE7B8";

    public static string SelectName(string header) => $"Move {header} cache";

    public static string MoveBackName(string header) => $"Move {header} cache back";

    /// <summary>The Dev Drive's letter with its colon, or a generic name if we cannot name it.</summary>
    private string DevDriveLabel =>
        ViewModel.Volumes.FirstOrDefault(row => row.IsDevDrive && row.Volume.DriveLetter.HasValue)?.Volume.DriveLetter
            is char letter
            ? $"{letter}:"
            : "your Dev Drive";

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Volumes.CollectionChanged += OnVolumesChanged;
        ViewModel.PackageCaches.CachesChanged += OnSourceChanged;
        ViewModel.PackageCaches.InventoryReset += OnSourceChanged;
        ViewModel.PackageCaches.PropertyChanged += OnCachesPropertyChanged;
        ViewModel.DriveHealth.PropertyChanged += OnDriveHealthChanged;
        ViewModel.PropertyChanged += OnViewModelChanged;
        PreferencesService.Changed += OnSourceChanged;

        CacheToMoveList.ItemsSource = _toMove;
        CacheOnDriveList.ItemsSource = _onDevDrive;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Volumes.CollectionChanged -= OnVolumesChanged;
        ViewModel.PackageCaches.CachesChanged -= OnSourceChanged;
        ViewModel.PackageCaches.InventoryReset -= OnSourceChanged;
        ViewModel.PackageCaches.PropertyChanged -= OnCachesPropertyChanged;
        ViewModel.DriveHealth.PropertyChanged -= OnDriveHealthChanged;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        PreferencesService.Changed -= OnSourceChanged;

        foreach (PackageCacheRowViewModel row in _hooked)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        _hooked.Clear();

        // The rows belong to the shared view model and outlive this page; let go of them so their
        // bindings do not keep these elements alive.
        CacheToMoveList.ItemsSource = null;
        CacheOnDriveList.ItemsSource = null;
        _toMove.Clear();
        _onDevDrive.Clear();

        _activeDialog?.Hide();
        if (Caches.IsConfirmingMoveAll)
        {
            Caches.CancelMoveAllCommand.Execute(null);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainPageViewModel.HasDevDrive))
        {
            RequestRefresh();
        }
    }

    private void OnVolumesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RequestRefresh();

    private void OnSourceChanged(object? sender, EventArgs e) => RequestRefresh();

    private void OnDriveHealthChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DriveHealthViewModel.PerformanceModeLineText))
        {
            RequestRefresh();
        }
    }

    private void OnCachesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PackageCachesViewModel.IsConfirmingMoveAll) when !Caches.IsConfirmingMoveAll:
                _moveAllDialog?.Hide();
                break;
            case nameof(PackageCachesViewModel.MoveAllHasFailures):
            case nameof(PackageCachesViewModel.IsCalculating):
            case nameof(PackageCachesViewModel.HasDevDrive):
                RequestRefresh();
                break;
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PackageCacheRowViewModel.IsSelectedForMove)
            or nameof(PackageCacheRowViewModel.CanSelectForMove))
        {
            UpdateMoveButton();
        }

        // Progress ticks must not churn the lists; only what decides a row's list, or its order, does.
        if (e.PropertyName is nameof(PackageCacheRowViewModel.CanMove)
            or nameof(PackageCacheRowViewModel.IsSet)
            or nameof(PackageCacheRowViewModel.IsMapped)
            or nameof(PackageCacheRowViewModel.IsMappedToDevDrive)
            or nameof(PackageCacheRowViewModel.SizeBytes))
        {
            RequestRefresh();
        }
    }

    /// <summary>One rebuild per dispatcher tick, however many sources moved during a load.</summary>
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

        string line = ViewModel.DriveHealth.PerformanceModeLineText;
        string state = line.StartsWith(PerformanceModePrefix, StringComparison.Ordinal)
            ? line[PerformanceModePrefix.Length..]
            : line;
        PerformanceModeState = state.Length > 0 ? char.ToUpperInvariant(state[0]) + state[1..] : state;
        Raise(nameof(PerformanceModeState));

        RebuildCaches();
    }

    private void RebuildDrives()
    {
        double lowFraction = PreferencesService.Current.LowFreeFraction;
        List<DriveCardViewModel> cards = [.. ViewModel.Volumes
            .Where(row => row.IsDevDrive)
            .Select(row => new DriveCardViewModel(row, lowFraction, "DevDrive"))];

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

    /// <summary>
    /// Sorts the shared rows into "to move" and "already on the Dev Drive", biggest first. Without a
    /// Dev Drive every cache we found is listed, so the panel still shows what one would be for.
    /// </summary>
    private void RebuildCaches()
    {
        HookRows();

        bool hasDevDrive = Caches.HasDevDrive;
        List<PackageCacheRowViewModel> toMove = [.. Caches.Caches
            .Where(row => hasDevDrive
                ? !row.IsOnDevDrive && (row.CanMove || row.IsMapped || row.Info.Detected)
                : row.Info.Detected || row.IsSet || row.IsMapped)
            .OrderByDescending(row => row.SizeBytes)
            .ThenBy(row => row.Header, StringComparer.CurrentCultureIgnoreCase)];
        List<PackageCacheRowViewModel> onDevDrive = hasDevDrive
            ? [.. Caches.Caches
                .Where(row => row.IsOnDevDrive)
                .OrderByDescending(row => row.SizeBytes)
                .ThenBy(row => row.Header, StringComparer.CurrentCultureIgnoreCase)]
            : [];

        Sync(_toMove, toMove);
        Sync(_onDevDrive, onDevDrive);
        UpdateCachePanel(hasDevDrive);

        static void Sync(ObservableCollection<PackageCacheRowViewModel> target, List<PackageCacheRowViewModel> rows)
        {
            if (target.SequenceEqual(rows))
            {
                return;
            }

            target.Clear();
            foreach (PackageCacheRowViewModel row in rows)
            {
                target.Add(row);
            }
        }
    }

    private void UpdateCachePanel(bool hasDevDrive)
    {
        // The inventory lists every supported tool, found or not, so an empty one has not loaded yet.
        bool loading = Caches.Caches.Count == 0;
        bool measuring = !loading && Caches.IsCalculating;
        bool any = _toMove.Count + _onDevDrive.Count > 0;
        string drive = DevDriveLabel;

        ulong total = 0UL;
        foreach (PackageCacheRowViewModel row in _toMove)
        {
            if (!hasDevDrive || row.CanMove)
            {
                total += row.SizeBytes;
            }
        }

        bool showTotal = !loading && !measuring && _toMove.Count > 0 && total > 0UL;
        CacheHeadline.Text = ByteSizeFormatter.Format(total);
        CacheHeadline.Visibility = UiHelpers.BoolToVisibility(showTotal);

        string count = _toMove.Count == 1 ? "1 package cache" : $"{_toMove.Count} package caches";
        CacheDetail.Text = loading ? "Looking for package caches\u2026"
            : measuring ? "Measuring package caches\u2026"
            : !any ? string.Empty
            : _toMove.Count == 0 ? $"All your package caches are on {drive}."
            : !hasDevDrive ? (showTotal ? "in package caches. Create a Dev Drive to move them." : $"{count} found. Create a Dev Drive to move them.")
            : showTotal ? $"of package caches could move to {drive}."
            : $"{count} could move to {drive}.";
        CacheDetail.Visibility = UiHelpers.BoolToVisibility(CacheDetail.Text.Length > 0);
        CacheProgress.Visibility = UiHelpers.BoolToVisibility(loading || measuring);

        CacheScroller.Visibility = UiHelpers.BoolToVisibility(any);
        CacheOnDriveHead.Visibility = UiHelpers.BoolToVisibility(_toMove.Count > 0 && _onDevDrive.Count > 0);
        CacheEmpty.Visibility = UiHelpers.BoolToVisibility(!loading && !any);
        CacheFooter.Visibility = UiHelpers.BoolToVisibility(any);

        MoveAllResultBar.Severity = Caches.MoveAllHasFailures ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        UpdateMoveButton();
    }

    /// <summary>"Move 4.2 GB" for the ticked caches; off when nothing is ticked or a move is running.</summary>
    private void UpdateMoveButton()
    {
        ulong selected = 0UL;
        int count = 0;
        foreach (PackageCacheRowViewModel row in _toMove)
        {
            if (row.CanSelectForMove && row.IsSelectedForMove)
            {
                selected += row.SizeBytes;
                count++;
            }
        }

        MoveSelectedButton.Content = _moving ? "Moving\u2026"
            : selected > 0UL ? $"Move {ByteSizeFormatter.Format(selected)}"
            : "Move";
        MoveSelectedButton.IsEnabled = !_moving && count > 0;
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

    /// <summary>Moves every ticked cache, after one confirmation, one at a time.</summary>
    private async void MoveAll_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || _moving || !IsLoaded || XamlRoot is null || !Caches.MoveAllCommand.CanExecute(null))
        {
            return;
        }

        List<PackageCacheRowViewModel> ticked = [.. _toMove.Where(row => row.CanSelectForMove && row.IsSelectedForMove)];
        try
        {
            Caches.MoveAllCommand.Execute(null);
            if (!Caches.IsConfirmingMoveAll)
            {
                return;
            }

            bool confirmed = await ShowConfirmationDialogAsync(
                ticked.Count == 1 ? $"Move {ticked[0].Header} cache?" : $"Move {ticked.Count} package caches?",
                Caches.MoveAllConfirmBodyText,
                "Move",
                "MoveAllConfirmDialog",
                isMoveAll: true);
            if (confirmed && IsLoaded && Caches.ConfirmMoveAllCommand.CanExecute(null))
            {
                _moving = true;
                UpdateMoveButton();
                await Caches.ConfirmMoveAllCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            ReportActionError("Move", ex);
        }
        finally
        {
            if (Caches.IsConfirmingMoveAll)
            {
                Caches.CancelMoveAllCommand.Execute(null);
            }

            if (_moving)
            {
                _moving = false;
                if (IsLoaded)
                {
                    UpdateMoveButton();
                }
            }
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

        const string Fallback = "Moving a cache relocates it to your Dev Drive and repoints the per-user "
            + "environment variable, leaving a reversible receipt so you can move it back at any time.";
        string docPath = Path.Combine(AppContext.BaseDirectory, "docs", "PackageCacheMoves.md");
        string body;
        try
        {
            body = File.Exists(docPath)
                ? await File.ReadAllTextAsync(docPath)
                : "The package-cache explainer couldn't be found in this build. " + Fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            body = "Couldn't open the package-cache explainer (" + ex.Message + "). " + Fallback;
        }

        try
        {
            await ShowDialogAsync(new ContentDialog
            {
                Title = "What moving a package cache does",
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = body,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 480,
                },
                CloseButtonText = "Got it",
                DefaultButton = ContentDialogButton.Close,
            }, "LearnAboutCacheMovesDialog");
        }
        catch (Exception ex)
        {
            ReportActionError("Opening cache guide", ex);
        }
    }

    private async Task<bool> ShowConfirmationDialogAsync(
        string title, string body, string action, string automationId, bool isMoveAll = false)
    {
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
        };

        if (isMoveAll)
        {
            _moveAllDialog = dialog;
        }

        try
        {
            return await ShowDialogAsync(dialog, automationId) == ContentDialogResult.Primary;
        }
        finally
        {
            if (isMoveAll)
            {
                _moveAllDialog = null;
            }
        }
    }

    /// <summary>
    /// Shows one dialog at a time. WinUI allows exactly one <see cref="ContentDialog"/> open, and a
    /// second <c>ShowAsync</c> throws from an <c>async void</c> handler — a double-click away.
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, string automationId)
    {
        if (_dialogOpen || !IsLoaded || XamlRoot is null)
        {
            return ContentDialogResult.None;
        }

        dialog.XamlRoot = XamlRoot;
        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) && style is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }

        AutomationProperties.SetAutomationId(dialog, automationId);

        _dialogOpen = true;
        _activeDialog = dialog;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
            _activeDialog = null;
        }
    }

    private void ReportActionError(string action, Exception exception)
    {
        System.Diagnostics.Debug.WriteLine($"[caches] {action} failed: {exception}");
        CachesActionError.Title = $"{action} failed";
        CachesActionError.Message = exception.Message;
        CachesActionError.IsOpen = true;
    }

    /// <summary>Opens the page named by the card's tag.</summary>
    private void Room_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && tag.Length > 0)
        {
            ShellPage.Current?.SelectNavItem(tag);
        }
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
