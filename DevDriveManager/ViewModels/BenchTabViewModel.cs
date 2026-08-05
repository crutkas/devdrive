using CommunityToolkit.Mvvm.ComponentModel;

namespace DevDriveManager.ViewModels;

/// <summary>Which question the Benchmarks room's table is answering.</summary>
public enum BenchTabKind
{
    /// <summary>The measurable workloads and how they ran. The room opens here.</summary>
    Workloads,

    /// <summary>The levers that would change those numbers.</summary>
    Suggestions,
}

/// <summary>
/// One tab in the Benchmarks room's table head.
/// </summary>
/// <remarks>
/// "How fast is this machine today" and "what would make it faster" are two questions about one
/// subject, and neither is a column of the other's table — a workload row has nowhere to put "move
/// your source onto the Dev Drive", and a lever has no milliseconds. Same split, same reason, as
/// Volumes and Filter drivers in the Drives room.
/// </remarks>
public sealed partial class BenchTabViewModel : ObservableObject
{
    public BenchTabViewModel(BenchTabKind kind, string title)
    {
        Kind = kind;
        Title = title;
    }

    /// <summary>Which view this is. Drives what the table card shows.</summary>
    public BenchTabKind Kind { get; }

    /// <summary>Tab label.</summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>How many rows this tab has. Zero is shown, not hidden — it is an answer.</summary>
    [ObservableProperty]
    public partial int Count { get; set; }

    /// <summary>True for the tab the table is currently showing.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Screen-reader label: the tab read as one phrase, including its count.</summary>
    public string AutomationName => $"{Title}, {Count}";

    /// <summary>Stable id for the UI suite. Derived from the kind, which never localises.</summary>
    public string AutomationId => $"BenchTab_{Kind}";

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(AutomationName));

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(AutomationName));
}
