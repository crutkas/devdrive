using CommunityToolkit.Mvvm.ComponentModel;

namespace DevDriveManager.ViewModels;

/// <summary>Which half of the cache inventory a tab covers.</summary>
public enum CacheTabKind
{
    /// <summary>Tools whose cache we found, wherever it currently lives. The room opens here.</summary>
    Detected,

    /// <summary>Tools we did not find. Offered for mapping rather than hidden.</summary>
    NotInstalled,
}

/// <summary>
/// One view in the Caches room's native selector.
/// </summary>
/// <remarks>
/// The room used to split rows four ways — all, needs action, on the Dev Drive, not installed — which
/// duplicated the Status column: whether a cache sits on C: or on the Dev Drive is already stated on
/// every row. Detected versus not installed is the one split no column carries, because an undetected
/// tool has no location, no size and no status to show. Two tabs, therefore, not four bands.
/// </remarks>
public sealed partial class CacheTabViewModel : ObservableObject
{
    public CacheTabViewModel(CacheTabKind kind, string title)
    {
        Kind = kind;
        Title = title;
    }

    /// <summary>Which half this is. Drives filtering of the table.</summary>
    public CacheTabKind Kind { get; }

    /// <summary>Tab label. Retitled at runtime when this PC has no Dev Drive.</summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>How many tools fall in this half.</summary>
    [ObservableProperty]
    public partial int Count { get; set; }

    /// <summary>Visible selector label, including the live count.</summary>
    public string DisplayText => $"{Title} ({Count})";

    /// <summary>Screen-reader label: the view read as one phrase, including its count.</summary>
    public string AutomationName => $"{Title}, {Count}";

    partial void OnCountChanged(int value)
    {
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(DisplayText));
    }

    partial void OnTitleChanged(string value)
    {
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(DisplayText));
    }
}
