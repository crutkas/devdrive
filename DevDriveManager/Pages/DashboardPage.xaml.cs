using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using DevDriveManager.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Pages;

/// <summary>
/// The "Dashboard" landing page: deliberately calm. It surfaces only what's actionable — the package
/// caches detected on the PC, a compact Dev Drive health summary, and a soft pointer to on-demand benchmarks.
/// Cache discovery remains useful without a Dev Drive; move actions and drive-only summaries appear only
/// when their prerequisite exists.
/// </summary>
public sealed partial class DashboardPage : Page
{
    private readonly HashSet<PackageCacheRowViewModel> _hooked = new();

    public MainPageViewModel ViewModel => App.Shared;

    /// <summary>Movable caches, or the detected inventory when no Dev Drive exists.</summary>
    public ObservableCollection<PackageCacheRowViewModel> MovableCaches { get; } = new();

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PackageCaches.CachesChanged += OnCachesChanged;
        ViewModel.PackageCaches.InventoryReset += OnInventoryReset;
        HookRows();
        RebuildMovable();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PackageCaches.CachesChanged -= OnCachesChanged;
        ViewModel.PackageCaches.InventoryReset -= OnInventoryReset;
        foreach (PackageCacheRowViewModel row in _hooked)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        _hooked.Clear();
    }

    private void OnCachesChanged(object? sender, System.EventArgs e)
    {
        HookRows();
        RebuildMovable();
    }

    private void OnInventoryReset(object? sender, System.EventArgs e)
    {
        HookRows();
        RebuildMovable();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PackageCacheRowViewModel.CanMove)
            or nameof(PackageCacheRowViewModel.IsSet)
            or nameof(PackageCacheRowViewModel.IsMapped)
            or nameof(PackageCacheRowViewModel.IsMappedToDevDrive))
        {
            RebuildMovable();
        }
    }

    private void HookRows()
    {
        foreach (PackageCacheRowViewModel row in _hooked.Where(r => !ViewModel.PackageCaches.Caches.Contains(r)).ToList())
        {
            row.PropertyChanged -= OnRowPropertyChanged;
            _hooked.Remove(row);
        }

        foreach (PackageCacheRowViewModel row in ViewModel.PackageCaches.Caches)
        {
            if (_hooked.Add(row))
            {
                row.PropertyChanged += OnRowPropertyChanged;
            }
        }
    }

    private void RebuildMovable()
    {
        MovableCaches.Clear();
        int index = 0;
        IEnumerable<PackageCacheRowViewModel> visible = ViewModel.PackageCaches.HasDevDrive
            ? ViewModel.PackageCaches.Caches.Where(
                r => (r.Info.Detected || r.IsMapped || r.IsSet) && !r.IsOnDevDrive)
            : ViewModel.PackageCaches.Caches.Where(r => r.Info.Detected);
        foreach (PackageCacheRowViewModel row in visible)
        {
            row.BandAlt = (index++ % 2) == 1;
            MovableCaches.Add(row);
        }
    }

    private void OpenPackageCaches_Click(object sender, RoutedEventArgs e) => ShellPage.Current?.SelectNavItem("caches");

    private void OpenDrives_Click(object sender, RoutedEventArgs e) => ShellPage.Current?.SelectNavItem("drives");

    private void OpenBenchmarks_Click(object sender, RoutedEventArgs e) => ShellPage.Current?.SelectNavItem("benchmarks");
}
