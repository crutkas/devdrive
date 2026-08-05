using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using DevDriveManager.Controls;
using DevDriveManager.ViewModels;
using DevDriveStorage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevDriveManager.Pages;

/// <summary>
/// The Benchmarks room: what a real build costs on each volume, and what would change that.
/// </summary>
/// <remarks>
/// <para>
/// The only room the comp does not draw, so its grammar is borrowed rather than copied: head strip,
/// tabbed table, a second visual that takes the slack, an inspector, a status bar. Two tabs, for the
/// same reason Drives has two — "how fast is this machine today" and "what would make it faster" are
/// different questions about one subject, and neither is a column of the other's table.
/// </para>
/// <para>
/// The suggestions have existed on <see cref="PerformanceSuiteViewModel"/>, with tests, since before
/// there was anywhere to put them. This room is where they finally render.
/// </para>
/// <para>
/// Nothing here measures anything itself. The suite lives on the shared <see cref="App.Shared"/> view
/// model so a run survives navigating away mid-suite — the third room to need that, and the reason it
/// is a rule rather than a fix.
/// </para>
/// </remarks>
public sealed partial class BenchmarksPage : Page, INotifyPropertyChanged
{
    /// <summary>
    /// Everything in a workload row that is not the flex column, in DIPs: WORKLOAD, C:, G: and
    /// DIFFERENCE (168 + 88 + 88 + 96), the ACTION button, the five 12px gaps, the row's own 14px
    /// horizontal padding, and the card's 1px border on each side.
    /// </summary>
    private const double WorkloadFixedColumnsWidth = 168 + 88 + 88 + 96 + 58 + (12 * 5) + 28 + 2;

    private readonly IVolumeProvider _volumeProvider = new SystemVolumeProvider();
    private readonly ObservableCollection<VolumeStripEntry> _volumeStripEntries = new();
    private readonly ObservableCollection<BenchWorkloadViewModel> _workloads = new();
    private readonly ObservableCollection<BenchSuggestionViewModel> _suggestions = new();

    private bool _isSubscribed;
    private double _cardWidth;
    private double _flexColumnWidth = 260;

    public BenchmarksPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainPageViewModel ViewModel => App.Shared;

    /// <summary>The two questions this room answers, in head order.</summary>
    public ObservableCollection<BenchTabViewModel> Tabs { get; } =
    [
        new(BenchTabKind.Workloads, "Workloads"),
        new(BenchTabKind.Suggestions, "Suggestions"),
    ];

    /// <summary>
    /// The strip above the room. This room compares two volumes, so the tiles are the two things every
    /// number below is about. No volume carries a movable band — nothing here moves bytes.
    /// </summary>
    public IReadOnlyList<VolumeStripEntry> VolumeStripEntries => _volumeStripEntries;

    /// <summary>The measurable workloads, one row each. Exposed read-only — see the remarks.</summary>
    /// <remarks>
    /// A mutable collection property on a <see cref="Page"/> poisons the XAML type generator: it treats
    /// the item type as XAML-constructible and emits setters for its members, which does not compile
    /// against an immutable row. The runtime type still notifies, so the list stays live.
    /// </remarks>
    public IReadOnlyList<BenchWorkloadViewModel> Workloads => _workloads;

    public IReadOnlyList<BenchSuggestionViewModel> Suggestions => _suggestions;

    /// <summary>The width the flex column absorbs, shared by the header and every row.</summary>
    public GridLength FlexColumnWidth => new(_flexColumnWidth);

    private BenchTabViewModel? _selectedTab;

    /// <summary>Which question the table is answering. Never null once the page has loaded.</summary>
    public BenchTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedTab))
            {
                return;
            }

            if (_selectedTab is not null)
            {
                _selectedTab.IsSelected = false;
            }

            _selectedTab = value;
            _selectedTab.IsSelected = true;
            Raise(nameof(SelectedTab));
            Raise(nameof(ShowWorkloads));
            Raise(nameof(ShowSuggestions));
            Raise(nameof(SubheadText));
        }
    }

    public bool ShowWorkloads => SelectedTab?.Kind != BenchTabKind.Suggestions;

    public bool ShowSuggestions => SelectedTab?.Kind == BenchTabKind.Suggestions;

    private BenchWorkloadViewModel? _selectedWorkload;

    /// <summary>The row the inspector is explaining.</summary>
    public BenchWorkloadViewModel? SelectedWorkload
    {
        get => _selectedWorkload;
        set
        {
            if (ReferenceEquals(value, _selectedWorkload))
            {
                return;
            }

            if (_selectedWorkload is not null)
            {
                _selectedWorkload.Row.PropertyChanged -= OnSelectedRowChanged;
            }

            _selectedWorkload = value;

            if (_selectedWorkload is not null)
            {
                _selectedWorkload.Row.PropertyChanged += OnSelectedRowChanged;
            }

            Raise(nameof(SelectedWorkload));
            RaiseInspector();
        }
    }

    // ---- Head ------------------------------------------------------------------------------------

    /// <summary>
    /// The volume letters, taken from the suite's own legends so the column heads, the inspector tiles
    /// and the bar legend can never disagree about which drive is which.
    /// </summary>
    public string SystemColumnHeader => LetterOf(ViewModel.Suite.SystemLegend, "C:");

    public string DevColumnHeader => LetterOf(ViewModel.Suite.DevLegend, "G:");

    public string SubheadText => ShowSuggestions
        ? ViewModel.Suite.UpsideIntro
        : ViewModel.Suite.Subtitle;

    /// <summary>
    /// The head chip. A count of what has been measured, not a speed figure: the question at the top of
    /// this room is "do I have numbers yet", and one fast workload out of four cannot answer it. Always
    /// the count form, so it never says the same sentence as the status bar underneath.
    /// </summary>
    public string StatusText => ViewModel.Suite.IsAvailable
        ? $"{_workloads.Count(w => w.IsMeasured)} of {_workloads.Count} measured"
        : "Not available on this PC";

    // ---- Inspector -------------------------------------------------------------------------------

    public string InspectorTitle => SelectedWorkload?.Name ?? "Select a workload";

    /// <summary>
    /// Which tools this workload stands in for. It is ecosystem membership, not a measurement, so it
    /// belongs in the inspector rather than in a table column beside the one number per drive.
    /// </summary>
    public string InspectorSubtitle => SelectedWorkload?.ToolsLine ?? "One row per measurable workload";

    public string InspectorSystemValue => SelectedWorkload?.SystemCell ?? "\u2014";

    public string InspectorDevValue => SelectedWorkload?.DevCell ?? "\u2014";

    public string InspectorWhat => SelectedWorkload?.Row.DetailsWhat is { Length: > 0 } what
        ? what
        : "Pick a row above and this explains what it builds, the command it runs, and how the timing is taken.";

    public string InspectorCommand => SelectedWorkload?.Row.DetailsCommand is { Length: > 0 } command
        ? command
        : "\u2014";

    public string InspectorMethod => SelectedWorkload?.Row.DetailsMethodology is { Length: > 0 } method
        ? method
        : "\u2014";

    /// <summary>
    /// The individual timings, not just the average — an average of three runs with one outlier is a
    /// different claim from three consistent runs, and only the raw list distinguishes them.
    /// </summary>
    public string InspectorRuns
    {
        get
        {
            if (SelectedWorkload?.Row is not { } row)
            {
                return "\u2014";
            }

            if (row.IsSkipped && row.SkipReasonText.Length > 0)
            {
                return row.SkipReasonText;
            }

            if (!row.HasRawRuns)
            {
                return "Not measured yet. Run this workload to see each individual timing.";
            }

            List<string> parts = [];
            if (row.SystemRunsText.Length > 0)
            {
                parts.Add(row.SystemRunsText);
            }

            if (row.DevRunsText.Length > 0)
            {
                parts.Add(row.DevRunsText);
            }

            if (row.HasPhaseBreakdown)
            {
                parts.Add(row.PhaseBreakdownText);
            }

            return parts.Count == 0 ? "\u2014" : string.Join(Environment.NewLine, parts);
        }
    }

    // ---- Lifecycle -------------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Subscribe();

        _selectedTab ??= Tabs[0];
        _selectedTab.IsSelected = true;
        Raise(nameof(SelectedTab));
        Raise(nameof(ShowWorkloads));
        Raise(nameof(ShowSuggestions));

        RefreshVolumeStrip();
        RebuildWorkloads();
        RebuildSuggestions();
        UpdateStatusBar();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    /// <summary>
    /// Symmetric with <see cref="Unsubscribe"/> and hung off <c>Loaded</c>, not the constructor: a page
    /// can leave and re-enter the visual tree without being rebuilt, and constructor-subscribe leaves
    /// the second visit silently dead — it renders, and then never updates again.
    /// </summary>
    private void Subscribe()
    {
        if (_isSubscribed)
        {
            return;
        }

        _isSubscribed = true;
        ViewModel.Suite.PropertyChanged += OnSuiteChanged;
        ViewModel.Ecosystems.Cards.CollectionChanged += OnCardsChanged;
        ViewModel.Volumes.CollectionChanged += OnVolumesChanged;
    }

    private void Unsubscribe()
    {
        if (!_isSubscribed)
        {
            return;
        }

        _isSubscribed = false;
        ViewModel.Suite.PropertyChanged -= OnSuiteChanged;
        ViewModel.Ecosystems.Cards.CollectionChanged -= OnCardsChanged;
        ViewModel.Volumes.CollectionChanged -= OnVolumesChanged;

        // The rows these wrappers listen to live on the shared suite and outlive the page, so a wrapper
        // that is still subscribed is held alive by the suite's event. NavigationCacheMode is Disabled,
        // meaning the page itself is thrown away on every exit -- without this, each visit strands a
        // whole generation of wrappers that can never be collected. OnLoaded rebuilds them.
        SelectedWorkload = null;
        foreach (BenchWorkloadViewModel workload in _workloads)
        {
            workload.Dispose();
        }

        _workloads.Clear();
    }

    private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildWorkloads();
        UpdateStatusBar();
    }

    /// <summary>
    /// Only the suite properties this room reads are worth reacting to. The suite assigns a dozen
    /// properties per progress tick and each one raises here.
    /// </summary>
    private void OnSuiteChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PerformanceSuiteViewModel.SystemLegend):
                Raise(nameof(SystemColumnHeader));
                break;

            case nameof(PerformanceSuiteViewModel.DevLegend):
                Raise(nameof(DevColumnHeader));
                break;

            case nameof(PerformanceSuiteViewModel.Subtitle):
                Raise(nameof(SubheadText));
                break;

            case nameof(PerformanceSuiteViewModel.IsAvailable):
            case nameof(PerformanceSuiteViewModel.IsRunning):
            case nameof(PerformanceSuiteViewModel.HasHeadline):
            case nameof(PerformanceSuiteViewModel.HeadlineValue):
            case nameof(PerformanceSuiteViewModel.RunStatusText):
                Raise(nameof(StatusText));
                UpdateStatusBar();
                break;

            case nameof(PerformanceSuiteViewModel.ShowTurnOnPerfModeLever):
            case nameof(PerformanceSuiteViewModel.ShowPerfModeOffCaveat):
            case nameof(PerformanceSuiteViewModel.ShowPerfModeManagedNote):
                RebuildSuggestions();
                break;
        }
    }

    /// <summary>A volume appearing or disappearing changes the strip this room compares against.</summary>
    private void OnVolumesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshVolumeStrip();

    /// <summary>
    /// The selected row finishing is the one case where the inspector must repaint without the
    /// selection changing — the numbers it is reading out arrive after the click.
    /// </summary>
    private void OnSelectedRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        RaiseInspector();
        Raise(nameof(StatusText));
        UpdateStatusBar();
    }

    // ---- Building --------------------------------------------------------------------------------

    /// <summary>
    /// Flattens the ecosystems that carry a measurable workload into table rows, preserving the
    /// selection across the rebuild by workload id — a rebuild replaces every row object, so holding
    /// the old reference would leave the inspector reading out a row no longer in the list.
    /// </summary>
    private void RebuildWorkloads()
    {
        string? selectedId = SelectedWorkload?.Row.Id;
        SelectedWorkload = null;

        foreach (BenchWorkloadViewModel old in _workloads)
        {
            old.Dispose();
        }

        _workloads.Clear();
        foreach (EcosystemCardViewModel card in ViewModel.Ecosystems.Cards)
        {
            if (card.Benchmark is { } row)
            {
                _workloads.Add(new BenchWorkloadViewModel(card.Glyph, card.Name, card.ToolsLine, row));
            }
        }

        Tab(BenchTabKind.Workloads).Count = _workloads.Count;

        SelectedWorkload = _workloads.FirstOrDefault(w => w.Row.Id == selectedId)
            ?? _workloads.FirstOrDefault();

        Raise(nameof(StatusText));
    }

    /// <summary>
    /// The levers, in descending order of what they are worth on a typical machine. Source placement
    /// first because it is the biggest win and the one the table above actually measures; the perf-mode
    /// rows are conditional so the room never suggests turning on something already on, or something
    /// group policy owns.
    /// </summary>
    private void RebuildSuggestions()
    {
        PerformanceSuiteViewModel suite = ViewModel.Suite;

        _suggestions.Clear();

        _suggestions.Add(new BenchSuggestionViewModel
        {
            Title = "Put your source on the Dev Drive",
            Detail = suite.UpsideSourceLever,
            ActionText = "Space",
            RoomTag = "space",
            AutomationId = "Suggestion_Source",
        });

        _suggestions.Add(new BenchSuggestionViewModel
        {
            Title = "Move your package caches",
            Detail = suite.UpsideCachesLever,
            ActionText = "Caches",
            RoomTag = "caches",
            AutomationId = "Suggestion_Caches",
        });

        if (suite.ShowTurnOnPerfModeLever)
        {
            _suggestions.Add(new BenchSuggestionViewModel
            {
                Title = "Turn on performance mode",
                Detail = suite.UpsidePerfModeLever,
                ActionText = "Drives",
                RoomTag = "drives",
                AutomationId = "Suggestion_PerfMode",
            });
        }

        // Advisory, not a lever: these two explain a number rather than offer to change one, so they
        // carry no verb. A button that goes nowhere is worse than no button.
        if (suite.ShowPerfModeOffCaveat)
        {
            _suggestions.Add(new BenchSuggestionViewModel
            {
                Title = "Why the numbers may read flat",
                Detail = suite.UpsidePerfModeCaveat,
                AutomationId = "Suggestion_PerfModeCaveat",
            });
        }

        if (suite.ShowPerfModeManagedNote)
        {
            _suggestions.Add(new BenchSuggestionViewModel
            {
                Title = "Performance mode is managed",
                Detail = suite.UpsidePerfModeManagedNote,
                AutomationId = "Suggestion_PerfModeManaged",
            });
        }

        Tab(BenchTabKind.Suggestions).Count = _suggestions.Count;
    }

    private void RefreshVolumeStrip()
    {
        IReadOnlyList<StorageVolume> volumes;
        try
        {
            volumes = _volumeProvider.GetFixedVolumes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A volume disappearing mid-session is a real event, not a bug. The room is still usable
            // without its strip, so this degrades rather than taking the page down.
            _volumeStripEntries.Clear();
            return;
        }

        string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);

        _volumeStripEntries.Clear();
        foreach (VolumeStripEntry entry in VolumeStrip.Build(volumes, systemRoot, new Dictionary<string, long>()))
        {
            _volumeStripEntries.Add(entry);
        }
    }

    /// <summary>
    /// The status bar carries the <i>interpretation</i>, not the count — the head chip already counts,
    /// and a room that says the same sentence twice teaches the reader to stop reading one of them.
    /// </summary>
    private void UpdateStatusBar()
    {
        StatusBar.Facts.Clear();

        if (!ViewModel.Suite.IsAvailable)
        {
            StatusBar.Facts.Add(new StatusFact("Benchmarks are not available on this PC", StatusEmphasis.Warn));
            return;
        }

        if (ViewModel.Suite.IsRunning)
        {
            StatusBar.Facts.Add(new StatusFact("Running"));
            StatusBar.Facts.Add(new StatusFact(ViewModel.Suite.RunStatusText));
            return;
        }

        if (ViewModel.Suite.HasHeadline)
        {
            StatusBar.Facts.Add(new StatusFact(ViewModel.Suite.HeadlineValue, StatusEmphasis.Good));
            StatusBar.Facts.Add(new StatusFact(ViewModel.Suite.HeadlineCaption));
        }
        else
        {
            StatusBar.Facts.Add(new StatusFact("Run a workload to see how the two volumes compare"));
        }

        StatusBar.Facts.Add(ViewModel.Suite.ShowSystemDriveBaseline
            ? new StatusFact("No Dev Drive to compare against", StatusEmphasis.Warn)
            : new StatusFact($"Comparing {SystemColumnHeader} with {DevColumnHeader}"));
    }

    private BenchTabViewModel Tab(BenchTabKind kind) => Tabs.First(t => t.Kind == kind);

    /// <summary>
    /// The drive letter out of a legend line such as "C: — system drive (NTFS, real-time antivirus)".
    /// Falls back rather than throwing: a column header is not worth crashing a room over.
    /// </summary>
    private static string LetterOf(string legend, string fallback)
    {
        int colon = legend.IndexOf(':');
        return colon is > 0 and < 4 ? legend[..(colon + 1)] : fallback;
    }

    private void RaiseInspector()
    {
        Raise(nameof(InspectorTitle));
        Raise(nameof(InspectorSubtitle));
        Raise(nameof(InspectorSystemValue));
        Raise(nameof(InspectorDevValue));
        Raise(nameof(InspectorWhat));
        Raise(nameof(InspectorCommand));
        Raise(nameof(InspectorMethod));
        Raise(nameof(InspectorRuns));
    }

    // ---- Layout and input ------------------------------------------------------------------------

    /// <summary>
    /// Measures the table card, not the header grid. Measuring the header would feed back on itself:
    /// a wider flex column grows the header's desired width, the grid is arranged at that desired
    /// width, and the next measurement reports the inflated number.
    /// </summary>
    private void TableCard_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        _cardWidth = args.NewSize.Width;

        double available = Math.Max(140, _cardWidth - WorkloadFixedColumnsWidth);
        if (Math.Abs(available - _flexColumnWidth) < 0.5)
        {
            return;
        }

        _flexColumnWidth = available;
        Raise(nameof(FlexColumnWidth));
    }

    /// <summary>
    /// Switches tabs. A Click handler rather than a bound command because the tab strip is chrome —
    /// the page owns which table is visible, not the shared ViewModel that every room shares.
    /// </summary>
    private void SelectTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BenchTabViewModel tab })
        {
            SelectedTab = tab;
        }
    }

    /// <summary>Opens the room that pulls the lever. The suggestion explains; the room acts.</summary>
    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BenchSuggestionViewModel suggestion } && suggestion.HasAction)
        {
            ShellPage.Current?.SelectNavItem(suggestion.RoomTag);
        }
    }

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    /// <summary>
    /// Forwards each row's AutomationId onto the generated <c>ListViewItem</c>.
    /// </summary>
    /// <remarks>
    /// The id used to live on the template's layout-only root Grid, which surfaces as a UIA Group
    /// with no SelectionItem pattern -- reachable, but not selectable by that id. A screen reader
    /// (and any automation on a desktop where injected input is refused) selects through
    /// SelectionItem, so the id has to be on the container. <c>AutomationProperties.Name</c> stays
    /// on the template root, where it describes the row's contents.
    /// </remarks>
    private void Workloads_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not null && args.Item is BenchWorkloadViewModel row)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(args.ItemContainer, row.RowAutomationId);
        }
    }
}
