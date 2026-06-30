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
/// caches still on the system drive (with a one-click "Move all"), a compact Dev Drive health summary, and
/// a soft pointer to on-demand benchmarks — plus the empty-state when there's no Dev Drive yet. Everything
/// binds to the shared <see cref="App.Shared"/> view model; the banded "still on C:" preview is a filtered
/// projection rebuilt as caches move.
/// </summary>
public sealed partial class DashboardPage : Page
{
    private readonly HashSet<PackageCacheRowViewModel> _hooked = new();

    public MainPageViewModel ViewModel => App.Shared;

    /// <summary>The detected caches still on the system drive — a calm, banded preview of what "Move all" will do.</summary>
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
        HookRows();
        RebuildMovable();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PackageCaches.CachesChanged -= OnCachesChanged;
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

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PackageCacheRowViewModel.CanMove)
            or nameof(PackageCacheRowViewModel.IsSet)
            or nameof(PackageCacheRowViewModel.IsMapped))
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
        foreach (PackageCacheRowViewModel row in ViewModel.PackageCaches.Caches.Where(r => r.CanMove))
        {
            row.BandAlt = (index++ % 2) == 1;
            MovableCaches.Add(row);
        }
    }

    private void OpenPackageCaches_Click(object sender, RoutedEventArgs e) => ShellPage.Current?.SelectNavItem("caches");

    private void OpenDrives_Click(object sender, RoutedEventArgs e) => ShellPage.Current?.SelectNavItem("drives");

    private void OpenBenchmarks_Click(object sender, RoutedEventArgs e) => ShellPage.Current?.SelectNavItem("benchmarks");
}
