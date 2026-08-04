using System.ComponentModel;
using DevDriveManager.Controls;
using DevDriveManager.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Pages;

/// <summary>
/// The Reclaim room: what can be deleted, why it is or is not safe, and what the machine looks like
/// afterwards.
/// </summary>
/// <remarks>
/// Scanning is not started automatically. A full scan is I/O bound and takes minutes on a real
/// machine, so it is something the user asks for rather than something that begins the moment they
/// glance at the room.
/// </remarks>
public sealed partial class ReclaimPage : Page
{
    public ReclaimViewModel ViewModel { get; } = App.SharedReclaim;

    public ReclaimPage()
    {
        InitializeComponent();

        // The status bar is rebuilt from the ViewModel rather than bound fact by fact: the facts are
        // a list whose length changes with state, so per-slot bindings would need a slot for every
        // possible fact plus a visibility rule for each.
        //
        // Subscribed on Loaded and released on Unloaded because the ViewModel is app-lifetime and
        // shared. A page that stayed subscribed would be held alive by it for the life of the app,
        // still updating a status bar nobody is looking at.
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateStatusBar();
        };

        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        UpdateStatusBar();

    private void UpdateStatusBar()
    {
        StatusBar.Facts.Clear();

        if (ViewModel.IsScanning)
        {
            StatusBar.Facts.Add(new StatusFact("Scanning"));
        }
        else if (ViewModel.ScanError is not null)
        {
            StatusBar.Facts.Add(new StatusFact("Scan failed", StatusEmphasis.Bad));
        }
        else if (!ViewModel.HasScanned)
        {
            StatusBar.Facts.Add(new StatusFact("Not scanned yet"));
        }
        else
        {
            StatusBar.Facts.Add(new StatusFact($"{ViewModel.FoundBytesText} found", StatusEmphasis.Good));
        }

        // Selection is shown whenever anything is ticked, including mid-scan: the number the user is
        // about to act on should never be the one that is hidden.
        if (ViewModel.SelectedBytes > 0)
        {
            StatusBar.Facts.Add(
                new StatusFact($"{ViewModel.SelectedBytesText} selected", StatusEmphasis.Warn));
        }
    }
}
