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
    private readonly Func<bool> _canRun;

    public PerfSuiteRowViewModel(
        string id,
        string name,
        string sub,
        char systemLetter,
        char devLetter,
        double maxBarWidth = 300d,
        Func<PerfSuiteRowViewModel, Task>? run = null,
        Func<bool>? canRun = null,
        bool hasComparison = true)
    {
        Id = id;
        Name = name;
        Sub = sub;
        _systemLetter = systemLetter;
        _devLetter = devLetter;
        _maxBarWidth = maxBarWidth;
        _canRun = canRun ?? (() => true);
        HasComparison = hasComparison;
        SystemDriveLabel = $"{systemLetter}:";
        DevDriveLabel = $"{devLetter}:";
        RunCommand = new AsyncRelayCommand(
            () => (run ?? (_ => Task.CompletedTask))(this),
            () => IsToolAvailable && _canRun());
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

    /// <summary>True after the required executable has been checked on this PC.</summary>
    [ObservableProperty]
    public partial bool IsToolAvailabilityKnown { get; set; } = true;

    /// <summary>True when the required executable can be launched by the benchmark service.</summary>
    [ObservableProperty]
    public partial bool IsToolAvailable { get; set; } = true;

    /// <summary>Visible while tool detection is pending or when the required executable is unavailable.</summary>
    [ObservableProperty]
    public partial bool ShowToolAvailability { get; set; }

    /// <summary>Plain-language explanation for a pending or unavailable benchmark tool.</summary>
    [ObservableProperty]
    public partial string ToolAvailabilityText { get; set; } = string.Empty;

    /// <summary>AutomationId for the required-tool availability message.</summary>
    public string ToolAvailabilityAutomationId => $"ToolAvailability_{Id}";

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

    /// <summary>True when this run includes a Dev Drive leg rather than a system-drive-only baseline.</summary>
    public bool HasComparison { get; }

    public bool ShowComparisonResult => HasComparison && IsDone;

    /// <summary>Applies a read-only executable probe to this row and refreshes its Run command.</summary>
    public void ApplyToolAvailability(InstalledToolInfo? tool)
    {
        IsToolAvailabilityKnown = tool is not null;
        IsToolAvailable = tool?.Found == true;
        ToolAvailabilityText = tool is null
            ? $"Checking whether {RequiredTool} is installed\u2026"
            : tool.Found
                ? string.Empty
                : $"Unavailable \u2014 {RequiredTool} is not installed or not on PATH.";
        ShowToolAvailability = !IsToolAvailable;
        if (IsQueued)
        {
            AutomationName = IsToolAvailable
                ? $"{Name}: queued."
                : $"{Name}: {ToolAvailabilityText}";
        }

        RunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Surfaces a failed executable probe without presenting the workload as runnable.</summary>
    public void MarkToolDetectionFailed()
    {
        IsToolAvailabilityKnown = false;
        IsToolAvailable = false;
        ToolAvailabilityText = $"Unavailable \u2014 the app could not verify that {RequiredTool} is installed.";
        ShowToolAvailability = true;
        if (IsQueued)
        {
            AutomationName = $"{Name}: {ToolAvailabilityText}";
        }

        RunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Dev-vs-system speedup for this row (0 when skipped or not computable) — fed to the headline.</summary>
    public double Speedup { get; private set; }

    // ---- Live state (exactly one of these is true) -------------------------------------------

    [ObservableProperty]
    public partial bool IsQueued { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowComparisonResult))]
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
        ShowToolAvailability = !IsToolAvailable;
        AutomationName = IsToolAvailable
            ? $"{Name}: queued."
            : $"{Name}: {ToolAvailabilityText}";
    }

    /// <summary>Moves the row into its running state with a status word (e.g. "running" or "run 2/3").</summary>
    public void MarkRunning(string status)
    {
        IsQueued = false;
        IsDone = false;
        IsSkipped = false;
        IsRunning = true;
        ShowToolAvailability = false;
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
        string devValue = HasComparison ? FormatSeconds(metric.DevSeconds) : string.Empty;

        // Surface the individual per-run times (incl. the discarded first) so the spread is visible in
        // the Details flyout — not just the reported median. The plain-language Sub is left untouched.
        SystemRunsText = FormatRuns(metric.SystemRuns, SystemDriveLabel, metric.SystemSeconds);
        DevRunsText = FormatRuns(metric.DevRuns, DevDriveLabel, metric.DevSeconds);
        HasRawRuns = metric.SystemRuns.Count > 0 || metric.DevRuns.Count > 0;
        PhaseBreakdownText = FormatPhaseBreakdown(metric);
        HasPhaseBreakdown = metric.TotalSeconds > 0d;

        if (HasComparison)
        {
            ApplyResult(metric.SystemSeconds, metric.DevSeconds, higherIsBetter: false, systemValue, devValue);
        }
        else
        {
            ApplyBaselineResult(systemValue);
        }
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
        ShowToolAvailability = false;
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

    private void ApplyBaselineResult(string systemValueText)
    {
        Speedup = 0d;
        SystemValueText = systemValueText;
        DevValueText = string.Empty;
        SystemBarWidth = _maxBarWidth;
        DevBarWidth = 0d;
        DeltaText = string.Empty;
        DeltaIsFavorable = false;
        IsQueued = false;
        IsRunning = false;
        IsSkipped = false;
        IsDone = true;
        AutomationName = $"{Name}: system-drive baseline {systemValueText} on {SystemDriveLabel}.";
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
