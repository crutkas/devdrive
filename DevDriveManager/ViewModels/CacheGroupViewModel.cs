using CommunityToolkit.Mvvm.ComponentModel;
using DevDriveCore;

namespace DevDriveManager.ViewModels;

/// <summary>Which band of the cache inventory a group covers.</summary>
public enum CacheGroupKind
{
    /// <summary>Every detected and undetected tool. The room opens here.</summary>
    All,

    /// <summary>Caches still on the system drive that could be moved.</summary>
    NeedsAction,

    /// <summary>Caches already living on the Dev Drive.</summary>
    OnDevDrive,

    /// <summary>Tools we did not find. Offered for mapping rather than hidden.</summary>
    NotInstalled,
}

/// <summary>
/// One entry in the Caches room's left rail: a band of the inventory with its count and total size.
/// </summary>
/// <remarks>
/// Presentation only — the rail groups the flat <see cref="PackageCachesViewModel.Caches"/> collection
/// and does not own any rows. It is a type rather than an anonymous tuple because the rail's
/// <c>DataTemplate</c> needs an <c>x:DataType</c> for compiled bindings, and because the counts and
/// totals are recomputed on every regroup and must raise change notifications when they do.
/// </remarks>
public sealed partial class CacheGroupViewModel : ObservableObject
{
    public CacheGroupViewModel(CacheGroupKind kind, string title, string glyph)
    {
        Kind = kind;
        Title = title;
        Glyph = glyph;
    }

    /// <summary>Which band this is. Drives filtering of the centre table.</summary>
    public CacheGroupKind Kind { get; }

    /// <summary>Rail label. Retitled at runtime when this PC has no Dev Drive.</summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>Segoe Fluent icon.</summary>
    public string Glyph { get; }

    /// <summary>How many tools fall in this band.</summary>
    [ObservableProperty]
    public partial int Count { get; set; }

    /// <summary>Combined size of the band's caches. Undetected tools contribute nothing.</summary>
    [ObservableProperty]
    public partial ulong TotalBytes { get; set; }

    /// <summary>"4 tools" / "1 tool" — the rail's sub-line.</summary>
    public string CountText => Count == 1 ? "1 tool" : $"{Count} tools";

    /// <summary>
    /// The band's total, or an em dash when it has no measurable bytes. A "0 B" here would claim we
    /// measured nothing, when the truth for "Not installed" is that there is nothing to measure.
    /// </summary>
    public string TotalText => TotalBytes == 0UL ? "\u2014" : ByteSizeFormatter.Format(TotalBytes);

    /// <summary>Screen-reader label: the whole row read as one sentence.</summary>
    public string AutomationName => $"{Title}, {CountText}, {TotalText}";

    /// <summary>Stable id for the UI suite. Derived from the kind, which never localises.</summary>
    public string AutomationId => $"CacheGroup_{Kind}";

    /// <summary>Applies a fresh count and total, raising the derived text with them.</summary>
    public void Set(int count, ulong totalBytes)
    {
        Count = count;
        TotalBytes = totalBytes;
    }

    partial void OnCountChanged(int value)
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(AutomationName));
    }

    partial void OnTotalBytesChanged(ulong value)
    {
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(AutomationName));
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(AutomationName));
}
