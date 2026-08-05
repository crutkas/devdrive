using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveManager.ViewModels;

/// <summary>
/// Top-level view-model for the unified per-ecosystem experience (Concept A). It is an ADAPTER that
/// composes the two existing engines verbatim — <see cref="PackageCachesViewModel"/> (real, reversible
/// move coordinator + detection + the new Map/remap path) and <see cref="PerformanceSuiteViewModel"/>
/// (the read-only workload benchmark) — and regroups their rows into one card per language ecosystem.
/// It adds NO new mutation or benchmark logic.
/// </summary>
/// <remarks>
/// Cards are rebuilt whenever <see cref="PackageCachesViewModel.CachesChanged"/> fires (after detection
/// repopulates the rows). Each card pulls its member tool rows from <see cref="PackageCachesViewModel.Caches"/>
/// and, only for the three benchmarked stacks, its workload row from
/// <see cref="PerformanceSuiteViewModel.BuildsGroup"/>. The honest summary counts only ecosystems that
/// are actually detected on this PC.
/// </remarks>
public partial class EcosystemsViewModel : ObservableObject
{
    private char _devLetter = 'G';
    private char _systemLetter = 'C';
    private string _systemRoot = "C:\\";
    private bool _hasDevDrive;

    public EcosystemsViewModel(PackageCachesViewModel caches, PerformanceSuiteViewModel suite)
    {
        Caches = caches ?? throw new ArgumentNullException(nameof(caches));
        Suite = suite ?? throw new ArgumentNullException(nameof(suite));
        Caches.CachesChanged += OnCachesChanged;
        Suite.ConfigurationChanged += OnSuiteConfigurationChanged;
    }

    /// <summary>Convenience factory composing the real package-cache and benchmark view-models.</summary>
    public static EcosystemsViewModel CreateDefault(Func<Action, bool>? dispatchToUi = null) =>
        new(PackageCachesViewModel.CreateDefault(), PerformanceSuiteViewModel.CreateDefault(dispatchToUi));

    /// <summary>The reused package-cache view-model (detection + reversible move + Map/remap).</summary>
    public PackageCachesViewModel Caches { get; }

    /// <summary>The reused performance-suite view-model (read-only workload benchmark).</summary>
    public PerformanceSuiteViewModel Suite { get; }

    /// <summary>One card per language ecosystem, rebuilt on detection.</summary>
    public ObservableCollection<EcosystemCardViewModel> Cards { get; } = new();

    /// <summary>Honest, non-salesy top summary, e.g. "2 of 4 ecosystems on your Dev Drive".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; set; } = "Scanning your developer tools\u2026";

    /// <summary>Always initializes cache discovery and system-drive benchmarks; a Dev Drive adds comparison.</summary>
    public void Initialize(string systemRoot, string? devRoot, char? devLetter, char systemLetter)
    {
        _systemRoot = systemRoot;
        _hasDevDrive = devLetter.HasValue;
        if (devLetter is char letter)
        {
            _devLetter = char.ToUpperInvariant(letter);
        }

        _systemLetter = systemLetter;

        // Seed benchmark rows first so cards capture the current baseline/comparison rows when caches load.
        Suite.Initialize(systemRoot, devRoot, devLetter, systemLetter);
        Caches.Initialize(systemRoot, devRoot, devLetter, systemLetter);
    }

    /// <summary>Suspends targets while drive state refreshes and immediately disables stale cache actions.</summary>
    public void SuspendDriveDependentActions(string cacheNoticeMessage)
    {
        _hasDevDrive = false;
        Suite.SuspendForDriveRefresh();
        Caches.SetDevDriveUnavailable(cacheNoticeMessage);
    }

    /// <summary>Falls back to system-drive benchmarks while preserving the read-only cache inventory.</summary>
    public void SetDevDriveUnavailable(string cacheNoticeMessage)
    {
        _hasDevDrive = false;
        Suite.Initialize(_systemRoot, devRoot: null, devLetter: null, systemLetter: _systemLetter);
        Caches.Initialize(
            _systemRoot,
            devRoot: null,
            devLetter: null,
            systemLetter: _systemLetter,
            unavailableNoticeMessage: cacheNoticeMessage);
    }

    /// <summary>Applies the authoritative per-volume performance verdict to the suite's conditional UI.</summary>
    public void ApplyPerformanceMode(EffectivePerformanceMode effective) => Suite.ApplyPerformanceMode(effective);

    /// <summary>Clears the perf-mode off-state UI after Drive health turns performance mode on.</summary>
    public void DismissPerformanceModeCaption() => Suite.DismissPerformanceModeCaption();

    private void OnCachesChanged(object? sender, EventArgs e) => BuildCards();

    private void OnSuiteConfigurationChanged(object? sender, EventArgs e)
    {
        if (Cards.Count > 0)
        {
            BuildCards();
        }
    }

    private void BuildCards()
    {
        foreach (EcosystemCardViewModel existing in Cards)
        {
            existing.PropertyChanged -= OnCardPropertyChanged;
            existing.Dispose();
        }

        Cards.Clear();

        foreach (EcosystemDefinition definition in EcosystemCatalog.Default)
        {
            List<PackageCacheRowViewModel> members = Caches.Caches
                .Where(row => definition.ToolNames.Any(
                    name => string.Equals(name, row.Header, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            PerfSuiteRowViewModel? benchmark = definition.HasBenchmark
                ? Suite.BuildsGroup.Rows.FirstOrDefault(
                    r => string.Equals(r.RequiredTool, definition.BenchmarkRequiredTool, StringComparison.OrdinalIgnoreCase))
                : null;

            var card = new EcosystemCardViewModel(
                definition, members, benchmark, _devLetter, _systemLetter, _hasDevDrive);
            card.PropertyChanged += OnCardPropertyChanged;
            Cards.Add(card);
        }

        SortCards();
        UpdateSummary();
    }

    /// <summary>
    /// Orders the cards so the ecosystems the user actually has (detected) sit at the top and the rest
    /// fall to the bottom, each group alphabetical by name (punctuation ignored, so ".NET" sorts under
    /// "N", "C++" under "C"). Re-applied whenever a card's detected state flips (e.g. after a Map).
    /// Reorders IN PLACE via <see cref="ObservableCollection{T}.Move"/>, so per-card expansion/state is
    /// preserved — no rebuild, no flicker.
    /// </summary>
    private void SortCards()
    {
        List<EcosystemCardViewModel> desired = Cards
            .OrderByDescending(c => c.IsBenchmarkOnly)
            .ThenByDescending(c => c.IsDetected)
            .ThenBy(c => SortKey(c.Name), StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int target = 0; target < desired.Count; target++)
        {
            int current = Cards.IndexOf(desired[target]);
            if (current != target)
            {
                Cards.Move(current, target);
            }
        }
    }

    /// <summary>Alphabetical sort key: visible letters/digits only, so ".NET" sorts under "N", "C++" under "C".</summary>
    private static string SortKey(string name) => new(name.Where(char.IsLetterOrDigit).ToArray());

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A card's detected state flipping (e.g. a tool just got mapped) can change which group it
        // belongs to, so re-apply the detected-first ordering before refreshing the summary.
        if (e.PropertyName == nameof(EcosystemCardViewModel.IsDetected))
        {
            SortCards();
        }

        if (e.PropertyName is nameof(EcosystemCardViewModel.OnDevDriveCount)
            or nameof(EcosystemCardViewModel.IsDetected))
        {
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        int detected = Cards.Count(c => c.IsDetected);
        int onDev = Cards.Count(c => c.OnDevDriveCount > 0);

        SummaryText = detected == 0
            ? _hasDevDrive
                ? "No package caches detected yet \u2014 map any tool you use to start tracking it."
                : "No package caches detected on this PC."
            : _hasDevDrive
                ? $"{onDev} of {detected} ecosystems on your Dev Drive."
                : $"{detected} ecosystems detected on this PC.";
    }
}
