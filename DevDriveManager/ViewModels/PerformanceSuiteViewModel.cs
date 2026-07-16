using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveManager.ViewModels;

/// <summary>
/// Drives the unified <b>Performance tests</b> suite (replacing the old "Disk speed test" + "Real
/// workload test" cards). One "Run tests" streams real, read-only benchmarks into a single grouped list —
/// Real-world builds (git / npm / dotnet / cargo) — sharing one side-by-side C:/G: bar grammar where the
/// faster drive is the longer accent bar. Each row exposes a plain-language description plus a Details
/// flyout (exact command, what it does, methodology, and the individual per-run times). Results land live
/// (queued → running → done/skipped) with a streaming "N× faster on average so far" headline, a live
/// progress strip, Cancel, and a per-row Run (each row's Run button measures just that tool's cache,
/// using the same fixture profile as "Run tests").
/// </summary>
/// <remarks>
/// The suite is <b>read-only benchmarking</b>: every engine writes only to bounded temp fixtures that
/// self-clean; nothing on a real drive or cache is touched. It reuses the existing engine
/// (<see cref="IWorkloadBenchmarkService"/>) and math (<see cref="PerfSuiteMath"/>,
/// <see cref="PerfSuiteAggregator"/>) verbatim — no benchmark is rebuilt. The synthetic raw disk-I/O group
/// was intentionally dropped: raw throughput is a poor Dev Drive test (noisy run-to-run), and on the common
/// same-physical-disk setup it reads ~1.0× and undercuts the real story — the Dev Drive's actual benefit
/// (asynchronous Defender scanning in performance mode) only shows up in real file-churn, i.e. the
/// Real-world builds above (see <see cref="DiskSpeedAbsenceNote"/>). The <see cref="ISpeedTestService"/>
/// engine stays library-only/green; it is simply unwired here. A separate "Suggestions" panel (surfaced
/// by the page at the bottom of the Dev Drive page) reframes cache placement as an upside rather than a
/// duplicate benchmark — see <see cref="UpsideHeader"/>.
/// </remarks>
public partial class PerformanceSuiteViewModel : ObservableObject
{
    private static readonly string[] BenchmarkTools = ["git", "npm", "dotnet", "cargo"];

    private readonly IWorkloadBenchmarkService _workload;
    private readonly IInstalledToolDetector? _toolDetector;
    private readonly Func<Action, bool> _dispatchToUi;

    private string _systemRoot = "C:\\";
    private string _devRoot = string.Empty;
    private char _devLetter = 'C';
    private char _systemLetter = 'C';
    private BenchmarkDriveConfiguration? _pendingConfiguration;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _toolDetectionCts;
    private IReadOnlyDictionary<string, InstalledToolInfo>? _toolSnapshot;
    private bool _toolAvailabilityReady;
    private bool _toolDetectionFailed;
    private int _toolDetectionGeneration;
    private PerfSuiteAggregator _aggregator = new(0);

    public PerformanceSuiteViewModel(
        IWorkloadBenchmarkService workload,
        IInstalledToolDetector? toolDetector = null,
        Func<Action, bool>? dispatchToUi = null)
    {
        _workload = workload ?? throw new ArgumentNullException(nameof(workload));
        _toolDetector = toolDetector;
        _dispatchToUi = dispatchToUi ?? (action =>
        {
            action();
            return true;
        });
        _toolAvailabilityReady = toolDetector is null;

        BuildsGroup = new PerfSuiteGroupViewModel("builds", "\uE756", "Real-world builds \u00B7 how your builds run today \u00B7 wall-clock seconds (lower is better)");

        SeedFixedRows();
        ApplyToolAvailabilityToRows();

        Groups = new ObservableCollection<PerfSuiteGroupViewModel> { BuildsGroup };
    }

    /// <summary>Convenience factory wiring the real workload engine.</summary>
    public static PerformanceSuiteViewModel CreateDefault(Func<Action, bool>? dispatchToUi = null)
    {
        IInstalledToolDetector detector = InstalledToolDetector.CreateDefault();
        return new PerformanceSuiteViewModel(
            WorkloadBenchmarkService.CreateDefault(detector),
            detector,
            dispatchToUi);
    }

    /// <summary>The suite's only benchmark group (Real-world builds); the page renders it, then the upside panel.</summary>
    public ObservableCollection<PerfSuiteGroupViewModel> Groups { get; }

    public PerfSuiteGroupViewModel BuildsGroup { get; }

    public event EventHandler? ConfigurationChanged;

    /// <summary>Current read-only tool probe, exposed internally so headless tests can await initialization.</summary>
    internal Task ToolDetectionTask { get; private set; } = Task.CompletedTask;

    /// <summary>True when benchmark results include a Dev Drive comparison leg.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSystemDriveBaseline))]
    public partial bool HasDevDrive { get; set; }

    /// <summary>False only while drive state is being refreshed and benchmark targets are intentionally suspended.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSystemDriveBaseline))]
    public partial bool IsAvailable { get; set; } = true;

    public bool ShowSystemDriveBaseline => IsAvailable && !HasDevDrive;

    /// <summary>Suite-header Segoe Fluent glyph (speed gauge).</summary>
    public string SuiteGlyph => "\uE9D9";

    /// <summary>Play glyph for the primary "Run tests" button.</summary>
    public string RunGlyph => "\uE768";

    /// <summary>Warning glyph for the calm perf-mode caveat line.</summary>
    public string CaveatGlyph => "\uE7BA";

    [ObservableProperty]
    public partial string Subtitle { get; set; } =
        "Measures real developer builds on your system drive now; add a Dev Drive later for a side-by-side comparison. " +
        "Cold first build \u00B7 first run discarded \u00B7 median of the timed runs \u00B7 lower time is better.";

    [ObservableProperty]
    public partial string SummaryText { get; set; } = "Run real workloads on C: to establish a system-drive baseline.";

    /// <summary>
    /// One calm, honest line explaining why raw disk-throughput rows aren't shown — so users don't wonder
    /// where they went. Raw throughput is noisy run-to-run and, on the common same-physical-disk setup,
    /// reads ~1.0×; the Dev Drive's real benefit is asynchronous antivirus scanning on the real builds above.
    /// </summary>
    public string DiskSpeedAbsenceNote { get; } =
        "Raw disk speed isn't shown \u2014 on most PCs your Dev Drive shares the same physical disk, and the " +
        "Dev Drive's real benefit is asynchronous antivirus scanning on the real builds above, not raw throughput.";

    /// <summary>
    /// One honest, suite-level line clarifying that the benchmarks build throwaway fixtures in a temp
    /// folder and delete them afterwards — the user's real code and caches are never touched.
    /// </summary>
    public string SuiteHonestyLine { get; } =
        "These are throwaway test projects the app generates in a temp folder and deletes afterward \u2014 " +
        "your real code and caches aren't touched. (npm / dotnet / cargo packages are real, pulled from " +
        "the real registries.)";

    // ---- "You could do more" upside panel (reframes cache placement as an upside, not a duplicate
    // ---- benchmark — see CHANGE 1b fallback in docs/SpeedTest.md) -----------------------------------

    /// <summary>Header for the upside panel (relocated to the page bottom and renamed "Suggestions").</summary>
    public string UpsideHeader { get; } = "Suggestions";

    /// <summary>Honest intro: the benchmarks above are "today"; these are levers that move the numbers.</summary>
    public string UpsideIntro { get; } =
        "The tests above show how things run today. The Dev Drive's bigger wins come from where your " +
        "files live \u2014 these are the levers, with an honest take on what each is worth on this machine.";

    /// <summary>Lever 1 — move package caches to the Dev Drive.</summary>
    public string UpsideCachesLever { get; } =
        "Move your package caches (npm, NuGet, Cargo) onto the Dev Drive. Helps most when a cache sees " +
        "lots of small-file churn; modest when builds restore from a warm cache.";

    /// <summary>Lever 2 — put source + build output on the Dev Drive (the bigger win).</summary>
    public string UpsideSourceLever { get; } =
        "Put your repo / source and build output on the Dev Drive. This is usually the bigger win \u2014 " +
        "it's exactly what the \"Real-world builds\" rows above measure.";

    /// <summary>Lever 3 — turn on Defender performance mode. Shown ONLY when perf mode is genuinely off
    /// and not managed by the organization (see <see cref="ShowTurnOnPerfModeLever"/>).</summary>
    public string UpsidePerfModeLever { get; } =
        "Turn on Defender performance mode so antivirus uses the lighter, asynchronous scan on the Dev Drive.";

    /// <summary>
    /// Honest caveat shown ONLY when performance mode is genuinely OFF (and not managed): synchronous
    /// antivirus scanning mutes the Dev Drive's advantage, so a real-build comparison can read closer to
    /// ~1.0\u00D7 until it's turned on. (Kept conditional so it never appears in the on/managed states.)
    /// </summary>
    public string UpsidePerfModeCaveat { get; } =
        "Heads up: performance mode is off, so Defender scans the Dev Drive synchronously \u2014 that mutes the " +
        "advantage and can make the builds above read closer to ~1.0\u00D7. Turning it on restores the " +
        "asynchronous scan.";

    /// <summary>
    /// Honest note shown ONLY when performance mode is MANAGED by the organization: asynchronous scanning
    /// is controlled by group policy, so there's nothing to turn on locally. Points to the authoritative
    /// per-volume source (Windows Security \u203A Dev Drive protection \u203A "See volumes").
    /// </summary>
    public string UpsidePerfModeManagedNote { get; } =
        "Performance mode is managed by your organization on this PC \u2014 asynchronous scanning for trusted " +
        "Dev Drives is controlled by group policy, so there's nothing to turn on here. The authoritative " +
        "per-volume status is Windows Security \u203A Dev Drive protection \u203A \u201CSee volumes\u201D.";

    [ObservableProperty]
    public partial string SystemLegend { get; set; } = "C: \u2014 system drive (NTFS, real-time antivirus)";

    [ObservableProperty]
    public partial string DevLegend { get; set; } = "G: \u2014 Dev Drive (ReFS)";

    // ---- Run state ----------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string RunStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int RunProgressValue { get; set; }

    [ObservableProperty]
    public partial int RunProgressMax { get; set; } = 1;

    [ObservableProperty]
    public partial bool HasHeadline { get; set; }

    [ObservableProperty]
    public partial string HeadlineValue { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HeadlineCaption { get; set; } = string.Empty;

    // ---- Perf-mode state (driven by the effective per-volume verdict via ApplyPerformanceMode) ----

    /// <summary>
    /// True when performance mode is genuinely OFF — show the calm, non-actionable caption in the
    /// Performance section pointing at Drive health (the authoritative enable control). Hidden in the
    /// on/managed/unknown states.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowPerformanceModeCaption { get; set; }

    /// <summary>
    /// True when performance mode is genuinely OFF and not managed — show the "Turn on performance mode"
    /// lever in the Suggestions section. CONDITIONAL: absent in the on/managed/unknown states.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowTurnOnPerfModeLever { get; set; }

    /// <summary>
    /// True when performance mode is genuinely OFF and not managed — show the "~1.0\u00D7 because perf
    /// mode is off" caveat in the Suggestions section. CONDITIONAL: absent in the on/managed/unknown states.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowPerfModeOffCaveat { get; set; }

    /// <summary>
    /// True when performance mode is MANAGED by the organization — show the honest "managed by your
    /// organization" note in the Suggestions section instead of the turn-on lever/caveat.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowPerfModeManagedNote { get; set; }

    /// <summary>The single demoted caveat caption — Drive health keeps the authoritative enable control.</summary>
    public string PerformanceModeCaptionMessage { get; } = PerformanceModeAdvisor.ManageInDriveHealthCaption;

    /// <summary>Configures a system-drive baseline, with an optional Dev Drive comparison leg.</summary>
    public void Initialize(string systemRoot, string? devRoot, char? devLetter, char systemLetter)
    {
        var configuration = new BenchmarkDriveConfiguration(systemRoot, devRoot, devLetter, systemLetter);
        if (IsRunning)
        {
            _pendingConfiguration = configuration;
            SuspendForDriveRefresh();
            return;
        }

        ApplyConfiguration(configuration);
    }

    /// <summary>Temporarily disables benchmark commands while drive state is being refreshed.</summary>
    public void SuspendForDriveRefresh()
    {
        _cts?.Cancel();
        CancelToolDetection();
        IsAvailable = false;
    }

    /// <summary>
    /// Applies the authoritative effective per-volume performance mode (from
    /// <see cref="PerformanceModeEvaluator"/>) to the conditional perf-mode UI. The "Turn on performance
    /// mode" lever, the perf-mode-off caveat, and the Performance-section caption appear ONLY when the
    /// mode is genuinely OFF; the managed note appears ONLY when it's managed by the organization. In the
    /// on/unknown states none of them appear (no false "Off" / no bogus "turn on" offer).
    /// </summary>
    public void ApplyPerformanceMode(EffectivePerformanceMode effective)
    {
        bool isOff = effective.State == PerformanceModeEffectiveness.Off;
        bool isManaged = effective.State == PerformanceModeEffectiveness.Managed;
        bool policyManaged = effective.PolicyEnforced;

        // C2: the caption, the "turn on" lever, and the off caveat are all user-actionable affordances —
        // suppress them when the state is policy-managed so they never render alongside the contradictory
        // "managed by your organization, nothing to turn on here" note.
        ShowPerformanceModeCaption = isOff && !policyManaged;
        // The local "turn on" lever applies only when the user can actually flip it: genuinely off AND not
        // controlled by group policy.
        ShowTurnOnPerfModeLever = isOff && !policyManaged;
        ShowPerfModeOffCaveat = isOff && !policyManaged;
        // The honest managed note replaces the lever when the org controls the (off or unreadable) state.
        ShowPerfModeManagedNote = isManaged || (isOff && policyManaged);
    }

    /// <summary>Clears the perf-mode off-state UI (called after Drive health turns performance mode on).</summary>
    public void DismissPerformanceModeCaption()
    {
        ShowPerformanceModeCaption = false;
        ShowTurnOnPerfModeLever = false;
        ShowPerfModeOffCaveat = false;
    }

    // ---- Commands -----------------------------------------------------------------------------

    private bool CanRunSuite() =>
        IsAvailable
        && !IsRunning
        && _toolAvailabilityReady
        && BuildsGroup.Rows.Any(row => row.IsToolAvailable);

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanRunSuite))]
    private Task RunAllAsync() => ExecuteRunAsync(Groups.Where(g => g.HasRows).ToList());

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    partial void OnIsRunningChanged(bool value) => NotifyRunStatesChanged();

    partial void OnIsAvailableChanged(bool value) => NotifyRunStatesChanged();

    private void NotifyRunStatesChanged()
    {
        RunAllCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();

        // Every per-row "Run" shares the suite's IsRunning gate, so re-evaluate them all: any run
        // (Run tests or a single row) disables all of them, and finishing re-enables them.
        foreach (PerfSuiteGroupViewModel group in Groups)
        {
            foreach (PerfSuiteRowViewModel row in group.Rows)
            {
                row.RunCommand.NotifyCanExecuteChanged();
            }
        }
    }

    // ---- Orchestration ------------------------------------------------------------------------

    private async Task ExecuteRunAsync(IReadOnlyList<PerfSuiteGroupViewModel> groups)
    {
        if (!IsAvailable || IsRunning || groups.Count == 0)
        {
            return;
        }

        foreach (PerfSuiteGroupViewModel group in groups)
        {
            foreach (PerfSuiteRowViewModel row in group.Rows)
            {
                row.Reset();
            }
        }

        int total = groups.Sum(g => g.Rows.Count);
        _aggregator = new PerfSuiteAggregator(total);
        RunProgressValue = 0;
        RunProgressMax = Math.Max(1, total);
        HasHeadline = false;
        HeadlineValue = string.Empty;
        HeadlineCaption = string.Empty;
        RunStatusText = "Starting\u2026";

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        IsRunning = true;

        try
        {
            foreach (PerfSuiteGroupViewModel group in groups)
            {
                token.ThrowIfCancellationRequested();
                await RunGroupCoreAsync(group, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the user cancels; finished rows keep their results and the rest stay skipped.
        }
        finally
        {
            IsRunning = false;
            RunStatusText = string.Empty;
            _cts?.Dispose();
            _cts = null;
            foreach (PerfSuiteGroupViewModel group in groups)
            {
                group.IsRunning = false;
            }

            ApplyPendingConfiguration();
            NotifyRunStatesChanged();
        }
    }

    private async Task RunGroupCoreAsync(PerfSuiteGroupViewModel group, CancellationToken token)
    {
        group.IsRunning = true;
        NotifyRunStatesChanged();
        try
        {
            // The suite's only benchmark group is Real-world builds.
            await RunBuildsAsync(group, token);
        }
        finally
        {
            group.IsRunning = false;
            NotifyRunStatesChanged();
        }
    }

    /// <summary>Real-world builds stream in as each completes; rows promote queued → running in catalogue order.</summary>
    private async Task RunBuildsAsync(PerfSuiteGroupViewModel group, CancellationToken token)
    {
        if (group.Rows.Count > 0)
        {
            group.Rows[0].MarkRunning("running");
            SetRunStatus(group.Rows[0]);
        }

        // Created on the UI thread so each reported metric marshals back here and lands live.
        var progress = new Progress<WorkloadMetric>(metric => OnBuildMetric(group, metric));
        try
        {
            if (HasDevDrive)
            {
                await _workload.RunAsync(_systemRoot, _devRoot, _devLetter, progress, token);
            }
            else
            {
                await _workload.RunSystemDriveAsync(_systemRoot, progress, token);
            }

            MarkUnfinishedSkipped(group, "no result");
        }
        catch (OperationCanceledException)
        {
            MarkUnfinishedSkipped(group, "Cancelled");
            throw;
        }
        catch (Exception ex)
        {
            MarkUnfinishedSkipped(group, Friendly(ex));
        }
    }

    private void OnBuildMetric(PerfSuiteGroupViewModel group, WorkloadMetric metric)
    {
        PerfSuiteRowViewModel? row = FindRow(group, BuildRowId(metric.Name));
        if (row is null)
        {
            return;
        }

        row.ApplyWorkload(metric);
        Aggregate(row);
        PromoteNextQueued(group);
    }

    // ---- Single-row run (the per-row "Run") ---------------------------------------------------

    /// <summary>
    /// Runs <em>one</em> build row on its own via <see cref="IWorkloadBenchmarkService.RunSingleAsync"/>.
    /// Only the targeted row is reset and re-run; the others keep their queued state or prior result. The
    /// headline is kept honest for a single run — the aggregator is initialized to a total of 1 so it
    /// reflects ONLY this row's "N× faster", never averaged against rows that didn't run. Shares the
    /// suite's <see cref="IsRunning"/> gate, so a single-row run and "Run tests" can never overlap.
    /// </summary>
    private async Task RunRowAsync(PerfSuiteRowViewModel row)
    {
        if (!IsAvailable || IsRunning)
        {
            return;
        }

        // Reset ONLY this row — the rest stay untouched (queued or their previous result).
        row.Reset();

        // Honest single-row headline: total = 1, so the headline shows just this row's result.
        _aggregator = new PerfSuiteAggregator(1);
        RunProgressValue = 0;
        RunProgressMax = 1;
        HasHeadline = false;
        HeadlineValue = string.Empty;
        HeadlineCaption = string.Empty;
        RunStatusText = "Starting\u2026";

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        IsRunning = true;

        row.MarkRunning("running");
        SetRunStatus(row);

        // Created on the UI thread so each reported phase/iteration marshals back here and lands live.
        var progress = new Progress<WorkloadRunProgress>(p => OnRowProgress(row, p));
        try
        {
            WorkloadMetric metric = HasDevDrive
                ? await _workload.RunSingleAsync(
                    row.RequiredTool, _systemRoot, _devRoot, _devLetter, iterations: 3, progress, token)
                : await _workload.RunSingleSystemDriveAsync(
                    row.RequiredTool, _systemRoot, iterations: 3, progress, token);
            row.ApplyWorkload(metric);
            Aggregate(row);
        }
        catch (OperationCanceledException)
        {
            row.MarkSkipped("Cancelled");
            Aggregate(row);
        }
        catch (Exception ex)
        {
            row.MarkSkipped(Friendly(ex));
            Aggregate(row);
        }
        finally
        {
            IsRunning = false;
            RunStatusText = string.Empty;
            _cts?.Dispose();
            _cts = null;
            ApplyPendingConfiguration();
            NotifyRunStatesChanged();
        }
    }

    /// <summary>Maps a single-row's fine-grained <see cref="WorkloadRunProgress"/> to honest live status.</summary>
    private void OnRowProgress(PerfSuiteRowViewModel row, WorkloadRunProgress progress)
    {
        string status = progress.Stage switch
        {
            WorkloadRunStage.Preparing => "Preparing",
            WorkloadRunStage.SystemDrive => $"{_systemLetter}: run {progress.Run}/{progress.TotalRuns}",
            WorkloadRunStage.DevDrive => $"{_devLetter}: run {progress.Run}/{progress.TotalRuns}",
            _ => "running",
        };
        row.SetRunStatus(status);

        char drive = progress.Stage == WorkloadRunStage.DevDrive ? _devLetter : _systemLetter;
        RunStatusText = progress.Stage == WorkloadRunStage.Preparing
            ? $"Preparing {row.Name}\u2026"
            : $"Running {row.Name} on {drive}:\u2026";
    }

    // ---- Headline + progress ------------------------------------------------------------------

    private void Aggregate(PerfSuiteRowViewModel row)
    {
        if (row.IsSkipped)
        {
            _aggregator.AddSkip();
        }
        else if (row.IsDone)
        {
            _aggregator.AddResult(row.Speedup);
        }

        UpdateHeadline();
    }

    private void UpdateHeadline()
    {
        RunProgressValue = _aggregator.Finished;
        RunProgressMax = Math.Max(1, _aggregator.Total);
        HeadlineValue = HasDevDrive
            ? _aggregator.HeadlineValue
            : $"{_aggregator.Finished}/{_aggregator.Total}";
        HeadlineCaption = HasDevDrive
            ? _aggregator.HeadlineCaption
            : "system-drive baseline workloads complete";
        HasHeadline = _aggregator.Finished > 0;
    }

    private void PromoteNextQueued(PerfSuiteGroupViewModel group)
    {
        PerfSuiteRowViewModel? next = group.Rows.FirstOrDefault(r => r.IsQueued);
        if (next is null)
        {
            return;
        }

        next.MarkRunning("running");
        SetRunStatus(next);
    }

    private void MarkUnfinishedSkipped(PerfSuiteGroupViewModel group, string reason)
    {
        foreach (PerfSuiteRowViewModel row in group.Rows.Where(r => r.IsRunning || r.IsQueued))
        {
            row.MarkSkipped(reason);
            _aggregator.AddSkip();
            UpdateHeadline();
        }
    }

    private void SetRunStatus(PerfSuiteRowViewModel row)
    {
        char drive = HasDevDrive ? _devLetter : _systemLetter;
        RunStatusText = $"Running {row.Name} on {drive}:\u2026";
    }

    // ---- Row scaffolding ----------------------------------------------------------------------

    private void SeedFixedRows()
    {
        string BuildMethodology = HasDevDrive
            ? "Cold first build, everything (package cache + source + output) on the test drive \u00B7 " +
              "3 timed runs per drive \u00B7 first run discarded \u00B7 median of the remaining 2."
            : "Cold first build on the system drive, with package cache + source + output co-located \u00B7 " +
              "3 timed runs \u00B7 first run discarded \u00B7 median of the remaining 2.";

        BuildsGroup.Rows.Add(new PerfSuiteRowViewModel("git-clone", "git clone", "Clones a 15,000-file repository from a local copy (no network)", _systemLetter, _devLetter, run: RunRowAsync, canRun: CanRunSuite, hasComparison: HasDevDrive)
        {
            RequiredTool = "git",
            DetailsWhat = "Generates a 15,000-file repository (300 folders \u00D7 50 small source files), commits it locally, then clones it onto the drive under test. No network.",
            DetailsCommand = "git clone --no-hardlinks <local bare repo> <target on the test drive>",
            DetailsMethodology = BuildMethodology,
        });
        BuildsGroup.Rows.Add(new PerfSuiteRowViewModel("npm-ci", "npm ci", "Clean-installs microsoft/vscode-eslint's committed package-lock.json (~213 packages) with npm ci, offline", _systemLetter, _devLetter, run: RunRowAsync, canRun: CanRunSuite, hasComparison: HasDevDrive)
        {
            RequiredTool = "npm",
            DetailsWhat = "Clean-installs Microsoft's vscode-eslint \u2014 microsoft/vscode-eslint at tag release/3.0.24 \u2014 from its committed package-lock.json (~213 real, transitive packages). The package cache is freshly copied onto the drive under test each run (a cold first install), then installed offline.",
            DetailsCommand = "copy npm cache onto the test drive  \u2192  npm ci --offline --no-audit --no-fund --cache <cache on the test drive>",
            DetailsMethodology = BuildMethodology,
        });
        BuildsGroup.Rows.Add(new PerfSuiteRowViewModel("dotnet-build", "dotnet build", "Builds Microsoft's System.Reactive (Rx.NET) for net6.0, offline", _systemLetter, _devLetter, run: RunRowAsync, canRun: CanRunSuite, hasComparison: HasDevDrive)
        {
            RequiredTool = "dotnet",
            DetailsWhat = "Builds Microsoft's System.Reactive (Rx.NET) \u2014 dotnet/reactive at tag rxnet-v6.1.0 \u2014 for net6.0. The NuGet cache is freshly copied onto the drive under test each run (a cold first build); an untimed offline restore re-points it there, then only the compile is timed.",
            DetailsCommand = "copy NuGet cache onto the test drive  \u2192  dotnet restore --source <offline> (untimed, re-points to the test-drive cache)  \u2192  dotnet build System.Reactive.csproj -f net6.0 -c Release --no-restore",
            DetailsMethodology = BuildMethodology,
        });
        BuildsGroup.Rows.Add(new PerfSuiteRowViewModel("cargo-build", "cargo build", "Builds Microsoft's Edit (the Rust console text editor), offline", _systemLetter, _devLetter, run: RunRowAsync, canRun: CanRunSuite, hasComparison: HasDevDrive)
        {
            RequiredTool = "cargo",
            DetailsWhat = "Compiles Microsoft's Edit \u2014 the Rust console text editor, microsoft/edit at tag v2.0.0 \u2014 into a fresh target/. The cargo registry cache is freshly copied onto the drive under test each run (a cold first build), then compiled offline.",
            DetailsCommand = "copy CARGO_HOME onto the test drive  \u2192  cargo build --offline   (CARGO_HOME on the test drive)",
            DetailsMethodology = BuildMethodology,
        });
    }

    private void RelabelRows()
    {
        // Drive letters are known only after Initialize; rebuild the fixed rows so their "C:"/"G:" labels match.
        BuildsGroup.Rows.Clear();
        SeedFixedRows();
        ApplyToolAvailabilityToRows();
    }

    private void ApplyConfiguration(BenchmarkDriveConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.SystemRoot);
        bool hasComparison = configuration.DevRoot is not null && configuration.DevLetter.HasValue;

        IsAvailable = false;
        PrepareToolDetection();
        _systemRoot = configuration.SystemRoot;
        _systemLetter = char.ToUpperInvariant(configuration.SystemLetter);
        _devRoot = configuration.DevRoot ?? string.Empty;
        _devLetter = configuration.DevLetter is char devLetter
            ? char.ToUpperInvariant(devLetter)
            : _systemLetter;
        HasDevDrive = hasComparison;

        if (hasComparison)
        {
            Subtitle =
                $"Compares your Dev Drive ({_devLetter}:) against your system drive ({_systemLetter}:) across real " +
                "developer builds. Cold first build, everything on the test drive \u00B7 first run discarded \u00B7 " +
                "median of the timed runs \u00B7 lower time is better.";
            SummaryText =
                $"Run real workloads on {_systemLetter}: and {_devLetter}: for a side-by-side comparison.";
            DevLegend = $"{_devLetter}: \u2014 Dev Drive (ReFS)";
        }
        else
        {
            Subtitle =
                $"Measures real developer builds on your system drive ({_systemLetter}:) now; add a Dev Drive later " +
                "for a side-by-side comparison. Cold first build \u00B7 first run discarded \u00B7 median of the timed " +
                "runs \u00B7 lower time is better.";
            SummaryText =
                $"Run real workloads on {_systemLetter}: to establish a system-drive baseline.";
            DevLegend = string.Empty;
        }

        SystemLegend = $"{_systemLetter}: \u2014 system drive (NTFS, real-time antivirus)";
        ResetRunResultState();
        RelabelRows();
        _pendingConfiguration = null;
        IsAvailable = true;
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        StartToolDetection();
    }

    private void PrepareToolDetection()
    {
        if (_toolDetector is null)
        {
            _toolAvailabilityReady = true;
            return;
        }

        CancelToolDetection();
        _toolSnapshot = null;
        _toolAvailabilityReady = false;
        _toolDetectionFailed = false;
    }

    private void StartToolDetection()
    {
        if (_toolDetector is null)
        {
            ToolDetectionTask = Task.CompletedTask;
            NotifyRunStatesChanged();
            return;
        }

        var cts = new CancellationTokenSource();
        _toolDetectionCts = cts;
        int generation = ++_toolDetectionGeneration;
        ToolDetectionTask = DetectToolsAsync(generation, cts);
        NotifyRunStatesChanged();
    }

    private async Task DetectToolsAsync(int generation, CancellationTokenSource cts)
    {
        try
        {
            IReadOnlyList<InstalledToolInfo> tools = await Task.Run(
                () => DetectBenchmarkTools(cts.Token),
                cts.Token).ConfigureAwait(false);

            PublishToolDetection(generation, () =>
            {
                _toolSnapshot = tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
                _toolAvailabilityReady = true;
                _toolDetectionFailed = false;
                ApplyToolAvailabilityToRows();
                NotifyRunStatesChanged();
            });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A newer drive configuration owns the rows and starts a fresh probe.
        }
        catch (Exception)
        {
            PublishToolDetection(generation, () =>
            {
                _toolSnapshot = null;
                _toolAvailabilityReady = true;
                _toolDetectionFailed = true;
                ApplyToolAvailabilityToRows();
                NotifyRunStatesChanged();
            });
        }
        finally
        {
            if (ReferenceEquals(_toolDetectionCts, cts))
            {
                _toolDetectionCts = null;
            }

            cts.Dispose();
        }
    }

    private void PublishToolDetection(int generation, Action update)
    {
        _dispatchToUi(() =>
        {
            if (generation == _toolDetectionGeneration)
            {
                update();
            }
        });
    }

    private IReadOnlyList<InstalledToolInfo> DetectBenchmarkTools(CancellationToken cancellationToken)
    {
        var tools = new List<InstalledToolInfo>(BenchmarkTools.Length);
        foreach (string toolName in BenchmarkTools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tools.Add(_toolDetector!.Detect(toolName, cancellationToken));
        }

        return tools;
    }

    private void ApplyToolAvailabilityToRows()
    {
        foreach (PerfSuiteRowViewModel row in BuildsGroup.Rows)
        {
            if (_toolDetector is null)
            {
                continue;
            }

            if (_toolDetectionFailed)
            {
                row.MarkToolDetectionFailed();
                continue;
            }

            if (!_toolAvailabilityReady || _toolSnapshot is null)
            {
                row.ApplyToolAvailability(tool: null);
                continue;
            }

            _toolSnapshot.TryGetValue(row.RequiredTool, out InstalledToolInfo? tool);
            row.ApplyToolAvailability(tool ?? new InstalledToolInfo
            {
                Name = row.RequiredTool,
                Found = false,
            });
        }
    }

    private void CancelToolDetection()
    {
        _toolDetectionGeneration++;
        _toolDetectionCts?.Cancel();
        _toolDetectionCts = null;
    }

    private void ApplyPendingConfiguration()
    {
        if (_pendingConfiguration is BenchmarkDriveConfiguration configuration)
        {
            ApplyConfiguration(configuration);
        }
    }

    private void ResetRunResultState()
    {
        _aggregator = new PerfSuiteAggregator(0);
        RunProgressValue = 0;
        RunProgressMax = 1;
        RunStatusText = string.Empty;
        HasHeadline = false;
        HeadlineValue = string.Empty;
        HeadlineCaption = string.Empty;
    }

    private sealed record BenchmarkDriveConfiguration(
        string SystemRoot,
        string? DevRoot,
        char? DevLetter,
        char SystemLetter);

    private static PerfSuiteRowViewModel? FindRow(PerfSuiteGroupViewModel group, string id) =>
        group.Rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));

    private static string BuildRowId(string name) => name switch
    {
        "git clone" => "git-clone",
        "npm ci" => "npm-ci",
        "dotnet build" => "dotnet-build",
        "cargo build" => "cargo-build",
        _ => string.Empty,
    };

    private static string Friendly(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? "couldn't complete" : ex.Message;
}
