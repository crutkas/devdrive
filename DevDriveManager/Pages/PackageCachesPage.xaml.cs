using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using DevDriveManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Pages;

/// <summary>
/// The "Package caches" page. It owns nothing but presentation: every detected tool's cache row, the
/// reversible move engine, and the "Move all" flow live on the shared <see cref="PackageCachesViewModel"/>.
/// The page's job is to group the flat <see cref="PackageCachesViewModel.Caches"/> collection into three
/// status bands. Without a Dev Drive, detected caches remain visible in a neutral "Detected on this PC"
/// band; with one, the same rows are split into Needs action / On your Dev Drive / Not installed.
/// </summary>
public sealed partial class PackageCachesPage : Page
{
    private readonly HashSet<PackageCacheRowViewModel> _hooked = new();

    public PackageCachesViewModel Caches => App.Shared.PackageCaches;

    /// <summary>Movable caches, or all detected caches when this PC has no Dev Drive.</summary>
    public ObservableCollection<PackageCacheRowViewModel> NeedsAction { get; } = new();

    /// <summary>Caches already on the Dev Drive (or mapped to a folder the user chose).</summary>
    public ObservableCollection<PackageCacheRowViewModel> OnDevDrive { get; } = new();

    /// <summary>Tools we didn't detect — offer to map them, never a dead end.</summary>
    public ObservableCollection<PackageCacheRowViewModel> NotInstalled { get; } = new();

    public PackageCachesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Caches.CachesChanged += OnCachesChanged;
        Caches.InventoryReset += OnInventoryReset;
        HookRows();
        Regroup();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Caches.CachesChanged -= OnCachesChanged;
        Caches.InventoryReset -= OnInventoryReset;
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

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only the properties that change which group a row belongs to should trigger a re-band; a move's
        // progress ticks (MoveProgressPercent, etc.) must not churn the lists.
        if (e.PropertyName is nameof(PackageCacheRowViewModel.CanMove)
            or nameof(PackageCacheRowViewModel.IsSet)
            or nameof(PackageCacheRowViewModel.IsMapped)
            or nameof(PackageCacheRowViewModel.IsMappedToDevDrive))
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

    /// <summary>Partition the flat cache list into the three status groups and re-stripe each one.</summary>
    private void Regroup()
    {
        NeedsAction.Clear();
        OnDevDrive.Clear();
        NotInstalled.Clear();

        bool detectionOnly = !Caches.HasDevDrive;
        foreach (PackageCacheRowViewModel row in Caches.Caches)
        {
            if (detectionOnly)
            {
                if (row.Info.Detected || row.IsSet || row.IsMapped)
                {
                    NeedsAction.Add(row);
                }
                else
                {
                    NotInstalled.Add(row);
                }
            }
            else if (row.IsOnDevDrive)
            {
                OnDevDrive.Add(row);
            }
            else if (row.CanMove || row.IsMapped || row.Info.Detected)
            {
                NeedsAction.Add(row);
            }
            else
            {
                NotInstalled.Add(row);
            }
        }

        Band(NeedsAction);
        Band(OnDevDrive);
        Band(NotInstalled);

        UpdateSection(
            NeedsActionSection,
            NeedsActionHeader,
            detectionOnly ? "Detected on this PC" : "Needs action",
            NeedsAction.Count);
        UpdateSection(OnDevDriveSection, OnDevDriveHeader, "On your Dev Drive", OnDevDrive.Count);
        UpdateSection(
            NotInstalledSection,
            NotInstalledHeader,
            detectionOnly ? "Not detected" : "Not installed",
            NotInstalled.Count);
    }

    private static void Band(ObservableCollection<PackageCacheRowViewModel> rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i].BandAlt = (i % 2) == 1;
        }
    }

    private static void UpdateSection(StackPanel section, TextBlock header, string title, int count)
    {
        section.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        header.Text = $"{title} ({count})";
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
