using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DevDriveManager.ViewModels;

/// <summary>
/// One group of the Performance test suite — Disk speed, Real-world builds, or Package caches. Holds the
/// group's streaming rows. The suite header's primary "Run tests" button runs every group, and each row
/// now carries its own "Run" so a single benchmark can be re-run on its own. The actual benchmarking lives
/// on the parent <see cref="PerformanceSuiteViewModel"/>; the group just hosts the rows.
/// </summary>
public partial class PerfSuiteGroupViewModel : ObservableObject
{
    public PerfSuiteGroupViewModel(string id, string glyph, string header)
    {
        Id = id;
        Glyph = glyph;
        Header = header;
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>Stable group id (e.g. "disk", "builds", "caches").</summary>
    public string Id { get; }

    /// <summary>Segoe Fluent glyph shown beside the group header.</summary>
    public string Glyph { get; }

    /// <summary>Group header line, e.g. "Disk speed · raw throughput".</summary>
    public string Header { get; }

    /// <summary>AutomationId for the group container.</summary>
    public string GroupAutomationId => $"SuiteGroup_{Id}";

    /// <summary>The streaming rows in this group (caches are populated once detection finds installed tools).</summary>
    public ObservableCollection<PerfSuiteRowViewModel> Rows { get; } = new();

    /// <summary>True when the group has at least one row to show (the caches group hides when nothing is installed).</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>True while this specific group is executing (drives the run-all progress accounting).</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; set; }
}
