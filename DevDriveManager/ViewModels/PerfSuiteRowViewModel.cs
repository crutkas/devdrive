using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveManager.ViewModels;

/// <summary>
/// One streaming row of the unified Performance test suite. A single instance walks the live state
/// machine <c>Queued → Running → Done</c> (or <c>Skipped</c>) as the suite executes, so the XAML can
/// bind the same template for every build row. Each result is folded through <see cref="PerfSuiteMath"/>
/// so the bars show the measured magnitude (longer = larger value) — build / cache seconds are
/// lower-is-better, so the <em>slower</em> drive (more seconds) draws the longer bar. The faster drive is
/// marked instead by the accent fill + the single green "N×" delta (shorter = faster). Each row also
/// carries its own <see cref="RunCommand"/> so a single benchmark can be re-run on its own (the parent
/// wires it up).
/// </summary>
public partial class PerfSuiteRowViewModel : ObservableObject
{
    private readonly char _systemLetter;
    private readonly char _devLetter;
    private readonly double _maxBarWidth;

    public PerfSuiteRowViewModel(
        string id,
        string name,
        string sub,
        char systemLetter,
        char devLetter,
        double maxBarWidth = 300d,
        Func<PerfSuiteRowViewModel, Task>? run = null,
        Func<bool>? canRun = null)
    {
        Id = id;
        Name = name;
        Sub = sub;
        _systemLetter = systemLetter;
        _devLetter = devLetter;
        _maxBarWidth = maxBarWidth;
        SystemDriveLabel = $"{systemLetter}:";
        DevDriveLabel = $"{devLetter}:";
        RunCommand = new AsyncRelayCommand(() => (run ?? (_ => Task.CompletedTask))(this), canRun ?? (() => true));
        Reset();
    }

    /// <summary>Stable row id used for the AutomationId (e.g. "seq-read", "npm-ci", "npm-cache").</summary>
    public string Id { get; }

    /// <summary>Row label, e.g. "Sequential read" / "git clone" / "npm cache".</summary>
    public string Name { get; }

    /// <summary>AutomationId for the row's name element (Borders have no UIA peer, so it lives on the TextBlock).</summary>
    public string RowAutomationId => $"TestRow_{Id}";

    /// <summary>Per-row "Run" command — re-runs just this benchmark on its own. Wired by the parent.</summary>
    public IAsyncRelayCommand RunCommand { get; }

    /// <summary>AutomationId for this row's "Run" button (per-row, stable, e.g. "RunRow_dotnet-build").</summary>
    public string RunButtonAutomationId => $"RunRow_{Id}";

    /// <summary>Accessible name for the per-row Run button (e.g. "Run dotnet build").</summary>
    public string RunAccessibleName => $"Run {Name}";

    /// <summary>The tool a cache row benchmarks (e.g. "npm", "dotnet", "cargo"); empty for build rows.</summary>
    public string RequiredTool { get; init; } = string.Empty;

    /// <summary>AutomationId for this row's "Details" affordance (per-row, stable, e.g. "RowDetails_npm-ci").</summary>
    public string DetailsAutomationId => $"RowDetails_{Id}";

    /// <summary>Accessible name for the Details affordance (icon-free, but explicit for screen readers).</summary>
    public string DetailsAccessibleName => $"Details for {Name}";

    /// <summary>AutomationId for the per-run-times line inside the Details flyout (e.g. "RowRuns_npm-ci").</summary>
    public string RawRunsAutomationId => $"RowRuns_{Id}";

    /// <summary>The exact command (or low-level I/O operation) this row runs — surfaced verbatim in the Details flyout.</summary>
    public string DetailsCommand { get; init; } = string.Empty;

    /// <summary>Plain-language description of what actually gets read / written / installed / cloned / compiled.</summary>
    public string DetailsWhat { get; init; } = string.Empty;

    /// <summary>One-line methodology (e.g. "Cold first build, everything on the test drive · 3 timed runs per drive · first discarded · median of 2").</summary>
    public string DetailsMethodology { get; init; } = string.Empty;

    /// <summary>True when this row has a Details affordance to show (concrete command / what-it-does copy).</summary>
    public bool HasDetails => !string.IsNullOrWhiteSpace(DetailsCommand) || !string.IsNullOrWhiteSpace(DetailsWhat);

    /// <summary>"C:" label for the system-drive bar line.</summary>
    public string SystemDriveLabel { get; }

    /// <summary>"G:" label for the Dev Drive bar line.</summary>
    public string DevDriveLabel { get; }

    /// <summary>Dev-vs-system speedup for this row (0 when skipped or not computable) — fed to the headline.</summary>
    public double Speedup { get; private set; }

    // ---- Live state (exactly one of these is true) -------------------------------------------

    [ObservableProperty]
    public partial bool IsQueued { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsDone { get; set; }

    [ObservableProperty]
    public partial bool IsSkipped { get; set; }

    // ---- Display ------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string Sub { get; set; }

    [ObservableProperty]
    public partial string RunStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SystemValueText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DevValueText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double SystemBarWidth { get; set; }

    [ObservableProperty]
    public partial double DevBarWidth { get; set; }

    [ObservableProperty]
    public partial string DeltaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool DeltaIsFavorable { get; set; }

    [ObservableProperty]
    public partial string SkipReasonText { get; set; } = string.Empty;

    /// <summary>Live screen-reader description for the whole row (also used for UI-test substring search).</summary>
    [ObservableProperty]
    public partial string AutomationName { get; set; } = string.Empty;

    /// <summary>Every system-drive run time (incl. the discarded first), formatted for the Details flyout.</summary>
    [ObservableProperty]
    public partial string SystemRunsText { get; set; } = string.Empty;

    /// <summary>Every Dev Drive run time (incl. the discarded first), formatted for the Details flyout.</summary>
    [ObservableProperty]
    public partial string DevRunsText { get; set; } = string.Empty;

    /// <summary>True once individual per-run times are available (after a run); false until then / when skipped.</summary>
    [ObservableProperty]
    public partial bool HasRawRuns { get; set; }

    /// <summary>Where the wall-clock time actually went (prepare + cold-cache copy + builds), shown after a run.</summary>
    [ObservableProperty]
    public partial string PhaseBreakdownText { get; set; } = string.Empty;

    /// <summary>True once a phase breakdown is available (after a measured run); drives its visibility.</summary>
    [ObservableProperty]
    public partial bool HasPhaseBreakdown { get; set; }

    /// <summary>Resets the row to its dimmed "Queued" state and clears any previous result.</summary>
    public void Reset()
    {
        IsQueued = true;
        IsRunning = false;
        IsDone = false;
        IsSkipped = false;
        RunStatusText = string.Empty;
        SystemValueText = string.Empty;
        DevValueText = string.Empty;
        SystemBarWidth = 0d;
        DevBarWidth = 0d;
        DeltaText = string.Empty;
        DeltaIsFavorable = false;
        SkipReasonText = string.Empty;
        SystemRunsText = string.Empty;
        DevRunsText = string.Empty;
        HasRawRuns = false;
        PhaseBreakdownText = string.Empty;
        HasPhaseBreakdown = false;
        Speedup = 0d;
        AutomationName = $"{Name}: queued.";
    }

    /// <summary>Moves the row into its running state with a status word (e.g. "running" or "run 2/3").</summary>
    public void MarkRunning(string status)
    {
        IsQueued = false;
        IsDone = false;
        IsSkipped = false;
        IsRunning = true;
        SetRunStatus(status);
    }

    /// <summary>Updates the running status in place (e.g. as cache iterations advance: "run 1/3" → "run 2/3").</summary>
    public void SetRunStatus(string status)
    {
        RunStatusText = status;
        AutomationName = $"{Name}: running, {status}.";
    }

    /// <summary>Completes the row from a workload metric (lower-is-better seconds), or skips it.</summary>
    public void ApplyWorkload(WorkloadMetric metric)
    {
        if (metric.Skipped)
        {
            MarkSkipped(metric.SkipReason ?? "unavailable");
            return;
        }

        string systemValue = FormatSeconds(metric.SystemSeconds);
        string devValue = FormatSeconds(metric.DevSeconds);

        // Surface the individual per-run times (incl. the discarded first) so the spread is visible in
        // the Details flyout — not just the reported median. The plain-language Sub is left untouched.
        SystemRunsText = FormatRuns(metric.SystemRuns, SystemDriveLabel, metric.SystemSeconds);
        DevRunsText = FormatRuns(metric.DevRuns, DevDriveLabel, metric.DevSeconds);
        HasRawRuns = metric.SystemRuns.Count > 0 || metric.DevRuns.Count > 0;
        PhaseBreakdownText = FormatPhaseBreakdown(metric);
        HasPhaseBreakdown = metric.TotalSeconds > 0d;

        ApplyResult(metric.SystemSeconds, metric.DevSeconds, higherIsBetter: false, systemValue, devValue);
    }

    /// <summary>
    /// Honest, plain-language breakdown of where the row's wall-clock went — so a ~15s reported result that
    /// actually took minutes is not a black box. The reported delta above is the MEDIAN BUILD only.
    /// </summary>
    private static string FormatPhaseBreakdown(WorkloadMetric metric)
    {
        int runs = metric.SystemRuns.Count + metric.DevRuns.Count;
        return $"Time spent: {FormatDuration(metric.TotalSeconds)} total \u2014 prepare (clone + warm cache) " +
            $"{FormatDuration(metric.PrepareSeconds)} \u00B7 cold-cache copy {FormatDuration(metric.SetupSeconds)} \u00B7 " +
            $"builds {FormatDuration(metric.BuildSeconds)} ({runs} runs). The result above is the median build only.";
    }

    /// <summary>Formats a duration as "&lt;1s", "42s", or "2m 05s".</summary>
    private static string FormatDuration(double seconds)
    {
        if (seconds < 1d)
        {
            return "<1s";
        }

        if (seconds < 60d)
        {
            return $"{(int)Math.Round(seconds)}s";
        }

        int minutes = (int)(seconds / 60d);
        int remainder = (int)Math.Round(seconds - (minutes * 60d));
        if (remainder == 60)
        {
            minutes++;
            remainder = 0;
        }

        return $"{minutes}m {remainder:00}s";
    }

    /// <summary>Marks the row skipped with a reason (missing tool, no network, low disk, cancelled…).</summary>
    public void MarkSkipped(string reason)
    {
        IsQueued = false;
        IsRunning = false;
        IsDone = false;
        IsSkipped = true;
        Speedup = 0d;
        SkipReasonText = $"Skipped \u2014 {reason}";
        AutomationName = $"{Name}: skipped. {reason}.";
    }

    private void ApplyResult(double systemValue, double devValue, bool higherIsBetter, string systemValueText, string devValueText)
    {
        (double systemFraction, double devFraction) = PerfSuiteMath.BarFractions(systemValue, devValue, higherIsBetter);
        Speedup = PerfSuiteMath.Speedup(systemValue, devValue, higherIsBetter);

        SystemValueText = systemValueText;
        DevValueText = devValueText;
        SystemBarWidth = BarWidth(systemFraction);
        DevBarWidth = BarWidth(devFraction);
        DeltaText = PerfSuiteMath.FormatSpeedup(Speedup);
        DeltaIsFavorable = PerfSuiteMath.IsFavorable(Speedup);

        IsQueued = false;
        IsRunning = false;
        IsSkipped = false;
        IsDone = true;

        AutomationName =
            $"{Name}: {systemValueText} on {SystemDriveLabel} versus {devValueText} on {DevDriveLabel}, " +
            $"{(DeltaIsFavorable ? $"{DeltaText} faster on the Dev Drive" : "no Dev Drive advantage")}.";
    }

    private double BarWidth(double fraction)
    {
        if (fraction <= 0d)
        {
            return 0d;
        }

        return Math.Max(4d, fraction * _maxBarWidth); // keep a sliver visible for tiny-but-nonzero values
    }

    private static string FormatSeconds(double seconds)
    {
        if (seconds <= 0d)
        {
            return "\u2014";
        }

        return seconds < 10d ? $"{seconds:0.00} s" : $"{seconds:0.0} s";
    }

    // Formats the individual per-run times for one drive, e.g.
    // "C:  4.21 s · 2.10 s · 2.05 s   (first discarded → median 2.08 s)".
    private static string FormatRuns(IReadOnlyList<double> runs, string driveLabel, double median)
    {
        if (runs is null || runs.Count == 0)
        {
            return string.Empty;
        }

        string joined = string.Join(" \u00B7 ", runs.Select(FormatRunSeconds));
        return runs.Count >= 2
            ? $"{driveLabel}  {joined}   (first discarded \u2192 median {FormatRunSeconds(median)})"
            : $"{driveLabel}  {joined}";
    }

    private static string FormatRunSeconds(double seconds) =>
        seconds < 10d ? $"{seconds:0.00} s" : $"{seconds:0.0} s";
}
