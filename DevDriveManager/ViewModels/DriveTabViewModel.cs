using CommunityToolkit.Mvvm.ComponentModel;

namespace DevDriveManager.ViewModels;

/// <summary>Which view of this machine's storage hardware a Drives tab covers.</summary>
public enum DriveTabKind
{
    /// <summary>Every fixed volume on the PC. The room opens here.</summary>
    Volumes,

    /// <summary>The filter drivers in the Dev Drive's I/O path.</summary>
    FilterDrivers,
}

/// <summary>
/// One tab in the Drives room's table head.
/// </summary>
/// <remarks>
/// Volumes and filter drivers are two different subjects about the same hardware — one is what the
/// disk is partitioned into, the other is what sits in the write path of one of those partitions.
/// They share a room because you arrive asking "is my Dev Drive set up right", and the answer needs
/// both; they are separate tabs because no column of a volume table has anywhere to put an altitude.
/// </remarks>
public sealed partial class DriveTabViewModel : ObservableObject
{
    public DriveTabViewModel(DriveTabKind kind, string title)
    {
        Kind = kind;
        Title = title;
    }

    /// <summary>Which view this is. Drives what the centre column shows.</summary>
    public DriveTabKind Kind { get; }

    /// <summary>Tab label.</summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>How many rows this tab has. Zero is shown, not hidden — it is an answer.</summary>
    [ObservableProperty]
    public partial int Count { get; set; }

    /// <summary>True for the tab the centre column is currently showing.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Screen-reader label: the tab read as one phrase, including its count.</summary>
    public string AutomationName => $"{Title}, {Count}";

    /// <summary>Stable id for the UI suite. Derived from the kind, which never localises.</summary>
    public string AutomationId => $"DriveTab_{Kind}";

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(AutomationName));

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(AutomationName));
}
