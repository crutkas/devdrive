using System.ComponentModel;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;
using DevDriveManager.ViewModels;
using NSubstitute;

namespace DevDriveCore.Tests;

/// <summary>
/// Unit tests for the unified Performance-suite ViewModels. They are linked into this test project
/// (they depend only on CommunityToolkit.Mvvm + DevDriveCore, no WinUI), so the row/group state
/// machine, the run-all-vs-per-group orchestration, skip handling, and the bar-sizing that draws the
/// bar proportional to the measured magnitude (so the SLOWER drive draws the longer bar for lower-is-better
/// seconds, while the faster drive is marked by the favorable "N×" delta) are all
/// verified headlessly with mocked engines.
/// </summary>
[TestClass]
public sealed class PerfSuiteRowViewModelTests
{
    private const double Tolerance = 1e-9;

    private static PerfSuiteRowViewModel NewRow(double maxBarWidth = 300d) =>
        new("seq-read", "Sequential read", "Large-block throughput", 'C', 'G', maxBarWidth);

    [TestMethod]
    public void NewRow_StartsQueued()
    {
        PerfSuiteRowViewModel row = NewRow();

        Assert.IsTrue(row.IsQueued);
        Assert.IsFalse(row.IsRunning);
        Assert.IsFalse(row.IsDone);
        Assert.IsFalse(row.IsSkipped);
        Assert.AreEqual("TestRow_seq-read", row.RowAutomationId);
        Assert.AreEqual("C:", row.SystemDriveLabel);
        Assert.AreEqual("G:", row.DevDriveLabel);
        StringAssert.Contains(row.AutomationName, "queued");
    }

    [TestMethod]
    public void MarkRunning_EntersRunningStateWithLiveStatus()
    {
        PerfSuiteRowViewModel row = NewRow();

        row.MarkRunning("running");
        Assert.IsTrue(row.IsRunning);
        Assert.IsFalse(row.IsQueued);
        StringAssert.Contains(row.AutomationName, "running");

        row.SetRunStatus("run 2/3");
        Assert.AreEqual("run 2/3", row.RunStatusText);
        StringAssert.Contains(row.AutomationName, "run 2/3");
    }

    [TestMethod]
    public void ApplyWorkload_LowerIsBetter_SlowerSystemDrawsTheLongerBar()
    {
        PerfSuiteRowViewModel row = new("git-clone", "git clone", "fixture", 'C', 'G');

        row.ApplyWorkload(new WorkloadMetric
        {
            Name = "git clone",
            SystemSeconds = 10d,
            DevSeconds = 5d,
        });

        Assert.IsTrue(row.IsDone);
        Assert.AreEqual(300d, row.SystemBarWidth, Tolerance);  // more seconds (slower) => longer bar
        Assert.AreEqual(150d, row.DevBarWidth, Tolerance);     // fewer seconds (faster) => its share
        Assert.IsGreaterThan(row.DevBarWidth, row.SystemBarWidth);
        Assert.IsTrue(row.DeltaIsFavorable);                   // faster on the Dev Drive => still favorable
        StringAssert.Contains(row.SystemValueText, "10");
        StringAssert.Contains(row.DevValueText, "5");
        StringAssert.Contains(row.DevValueText, "s");
    }

    [TestMethod]
    public void ApplyWorkload_DoesNotOverrideSub_PlainLanguageSubtitlePreserved()
    {
        // CHANGE 1a: the plain-language subtitle set at construction must SURVIVE a result. The
        // workload's terse Detail string no longer replaces it — transparency now lives in the
        // per-row Details flyout, not by mutating the subtitle.
        const string plain = "Installs ~600 npm packages with npm ci, offline";
        PerfSuiteRowViewModel row = new("npm-ci", "npm ci", plain, 'C', 'G');

        row.ApplyWorkload(new WorkloadMetric
        {
            Name = "npm ci",
            Detail = "1,200 files",
            SystemSeconds = 8d,
            DevSeconds = 6d,
        });

        Assert.AreEqual(plain, row.Sub);
    }

    [TestMethod]
    public void ApplyWorkload_SurfacesIndividualRawRunTimes_ForBothDrives()
    {
        // CHANGE 1a: the Details flyout shows EACH run (incl. the discarded first), so ApplyWorkload
        // must surface SystemRuns/DevRuns — not just the median — to make the spread visible.
        PerfSuiteRowViewModel row = new("npm-ci", "npm ci", "offline install", 'C', 'G');

        row.ApplyWorkload(new WorkloadMetric
        {
            Name = "npm ci",
            SystemSeconds = 8d,
            DevSeconds = 6d,
            SystemRuns = new[] { 8.4d, 8.0d, 7.9d },
            DevRuns = new[] { 6.3d, 6.0d, 5.8d },
        });

        Assert.IsTrue(row.HasRawRuns);
        StringAssert.Contains(row.SystemRunsText, "C:");
        StringAssert.Contains(row.DevRunsText, "G:");
        StringAssert.Contains(row.SystemRunsText, "8.4"); // first (discarded) run still shown
        StringAssert.Contains(row.DevRunsText, "5.8");
        StringAssert.Contains(row.SystemRunsText, "median"); // methodology made explicit inline
    }

    [TestMethod]
    public void ApplyWorkload_NoRawRuns_LeavesRawRunFlagFalse()
    {
        PerfSuiteRowViewModel row = new("git-clone", "git clone", "clone a repo", 'C', 'G');

        row.ApplyWorkload(new WorkloadMetric
        {
            Name = "git clone",
            SystemSeconds = 10d,
            DevSeconds = 5d,
        });

        Assert.IsFalse(row.HasRawRuns);
        Assert.AreEqual(string.Empty, row.SystemRunsText);
        Assert.AreEqual(string.Empty, row.DevRunsText);
    }

    [TestMethod]
    public void DetailsCopy_StableAutomationIdsAndHasDetailsFlag()
    {
        // CHANGE 1a: a row that carries Details copy exposes HasDetails + stable, per-row AutomationIds
        // for the Details affordance and the raw-runs line, plus a screen-reader-friendly name.
        PerfSuiteRowViewModel row = new("npm-ci", "npm ci", "offline install", 'C', 'G')
        {
            DetailsWhat = "Installs ~600 npm packages.",
            DetailsCommand = "npm ci --offline",
            DetailsMethodology = "3 timed runs, first discarded, median of 2.",
        };

        Assert.IsTrue(row.HasDetails);
        Assert.AreEqual("RowDetails_npm-ci", row.DetailsAutomationId);
        Assert.AreEqual("RowRuns_npm-ci", row.RawRunsAutomationId);
        StringAssert.Contains(row.DetailsAccessibleName, "npm ci");
    }

    [TestMethod]
    public void RunAffordance_HasStableAutomationIdAndAccessibleName()
    {
        // The per-row "Run" re-runs just this benchmark; its AutomationId/Name are stable and derived
        // from the row id/name (e.g. "RunRow_dotnet-build", "Run dotnet build").
        PerfSuiteRowViewModel row = new("dotnet-build", "dotnet build", "builds Rx.NET", 'C', 'G');

        Assert.AreEqual("RunRow_dotnet-build", row.RunButtonAutomationId);
        Assert.AreEqual("Run dotnet build", row.RunAccessibleName);
        Assert.IsNotNull(row.RunCommand);
        Assert.IsTrue(row.RunCommand.CanExecute(null)); // a standalone row defaults to runnable
    }

    [TestMethod]
    public void MissingRequiredTool_DisablesRunAndSurfacesReason()
    {
        PerfSuiteRowViewModel row = new("cargo-build", "cargo build", "builds Edit", 'C', 'G')
        {
            RequiredTool = "cargo",
        };

        row.ApplyToolAvailability(new InstalledToolInfo { Name = "cargo", Found = false });

        Assert.IsFalse(row.RunCommand.CanExecute(null));
        Assert.IsTrue(row.ShowToolAvailability);
        StringAssert.Contains(row.ToolAvailabilityText, "cargo");
        StringAssert.Contains(row.ToolAvailabilityText, "PATH");
    }

    [TestMethod]
    public void DetailsCopy_AbsentByDefault_HasDetailsFalse()
    {
        PerfSuiteRowViewModel row = NewRow();

        Assert.IsFalse(row.HasDetails);
        Assert.AreEqual(string.Empty, row.DetailsCommand);
        Assert.AreEqual(string.Empty, row.DetailsWhat);
    }

    [TestMethod]
    public void ApplyWorkload_Skipped_MarksRowSkippedWithReason()
    {
        PerfSuiteRowViewModel row = new("npm-ci", "npm ci", "node_modules install", 'C', 'G');

        row.ApplyWorkload(new WorkloadMetric
        {
            Name = "npm ci",
            Skipped = true,
            SkipReason = "npm is not installed",
        });

        Assert.IsTrue(row.IsSkipped);
        Assert.IsFalse(row.IsDone);
        StringAssert.Contains(row.SkipReasonText, "Skipped");
        StringAssert.Contains(row.SkipReasonText, "npm is not installed");
        StringAssert.Contains(row.AutomationName, "skipped");
    }

    [TestMethod]
    public void MarkSkipped_SetsReasonText()
    {
        PerfSuiteRowViewModel row = NewRow();

        row.MarkSkipped("no network");

        Assert.IsTrue(row.IsSkipped);
        StringAssert.Contains(row.SkipReasonText, "no network");
    }

    [TestMethod]
    public void BarWidth_TinyButNonZeroFraction_KeepsAMinimumSliver()
    {
        PerfSuiteRowViewModel row = NewRow();

        row.ApplyWorkload(new WorkloadMetric
        {
            Name = "Sequential read",
            SystemSeconds = 100000d,
            DevSeconds = 1d,
        });

        Assert.AreEqual(300d, row.SystemBarWidth, Tolerance);  // far more seconds (slower) => longest bar
        Assert.AreEqual(4d, row.DevBarWidth, Tolerance); // tiny share floored to a visible sliver, never 0
    }

    [TestMethod]
    public void Reset_ClearsAnyPreviousResult()
    {
        PerfSuiteRowViewModel row = NewRow();
        row.ApplyWorkload(new WorkloadMetric { Name = "Sequential read", SystemSeconds = 10d, DevSeconds = 5d });

        row.Reset();

        Assert.IsTrue(row.IsQueued);
        Assert.IsFalse(row.IsDone);
        Assert.AreEqual(0d, row.SystemBarWidth, Tolerance);
        Assert.AreEqual(0d, row.DevBarWidth, Tolerance);
        Assert.AreEqual(string.Empty, row.DeltaText);
    }
}

/// <summary>Tests for <see cref="PerfSuiteGroupViewModel"/> — row presence + the group automation id.</summary>
[TestClass]
public sealed class PerfSuiteGroupViewModelTests
{
    private static PerfSuiteGroupViewModel NewGroup() =>
        new("builds", "\uE756", "Real-world builds");

    [TestMethod]
    public void HasRows_TogglesWhenRowsAreAdded()
    {
        PerfSuiteGroupViewModel group = NewGroup();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)group).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.IsFalse(group.HasRows);

        group.Rows.Add(new PerfSuiteRowViewModel("r", "n", "s", 'C', 'G'));

        Assert.IsTrue(group.HasRows);
        CollectionAssert.Contains(changed, nameof(PerfSuiteGroupViewModel.HasRows));
    }

    [TestMethod]
    public void AutomationId_FollowsTheGroupId()
    {
        PerfSuiteGroupViewModel group = NewGroup();

        Assert.AreEqual("SuiteGroup_builds", group.GroupAutomationId);
    }
}

/// <summary>
/// Tests for <see cref="PerformanceSuiteViewModel"/> — the orchestrator. The workload engine is mocked so
/// the run-all / per-group orchestration, the command CanExecute gating, and the seeded build rows are
/// verified headlessly without any real benchmarking or network. (Live build-row resolution streams via
/// <see cref="System.Progress{T}"/> off the UI thread, so it is covered by the UI test, not here. The
/// synthetic raw disk-I/O group was removed — its <see cref="ISpeedTestService"/> engine stays green in its
/// own tests, just unwired from the suite.)
/// </summary>
[TestClass]
public sealed class PerformanceSuiteViewModelTests
{
    private static PerformanceSuiteViewModel CreateSut(out IWorkloadBenchmarkService workload)
    {
        workload = Substitute.For<IWorkloadBenchmarkService>();
        var sut = new PerformanceSuiteViewModel(workload);
        sut.Initialize(@"C:\", @"G:\", 'G', 'C');
        return sut;
    }

    // Stubs the single-row engine so a per-row Run resolves to a fixed metric regardless of arguments.
    private static void StubSingle(IWorkloadBenchmarkService workload, WorkloadMetric metric) =>
        workload.RunSingleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<char>(),
            Arg.Any<int>(), Arg.Any<IProgress<WorkloadRunProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(metric));

    [TestMethod]
    public void Construction_HasOneBuildsGroupWithFixedBuildRows()
    {
        // The synthetic raw disk-I/O group was dropped: raw throughput is a poor Dev Drive test (noisy
        // run-to-run, and ~1.0x on the common same-physical-disk setup). The suite's only benchmark group is
        // now Real-world builds; the absence note + the "You could do more" upside panel carry the rest.
        PerformanceSuiteViewModel sut = CreateSut(out _);

        Assert.HasCount(1, sut.Groups);
        Assert.AreSame(sut.BuildsGroup, sut.Groups[0]);
        Assert.AreEqual("builds", sut.BuildsGroup.Id);
        Assert.HasCount(4, sut.BuildsGroup.Rows);
        Assert.IsFalse(sut.IsRunning);
        Assert.IsTrue(sut.RunAllCommand.CanExecute(null));
        Assert.IsFalse(sut.CancelCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task WithoutDevDrive_RunCommandsProduceSystemDriveBaseline()
    {
        IWorkloadBenchmarkService workload = Substitute.For<IWorkloadBenchmarkService>();
        workload.RunSingleSystemDriveAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<IProgress<WorkloadRunProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(new WorkloadMetric
            {
                Name = "dotnet build",
                SystemSeconds = 8d,
                SystemRuns = new[] { 9d, 8d, 8d },
            });
        var sut = new PerformanceSuiteViewModel(workload);
        sut.Initialize(@"C:\", devRoot: null, devLetter: null, systemLetter: 'C');
        PerfSuiteRowViewModel row = sut.BuildsGroup.Rows.Single(r => r.Id == "dotnet-build");

        Assert.IsFalse(sut.HasDevDrive);
        Assert.IsTrue(sut.ShowSystemDriveBaseline);
        Assert.IsTrue(sut.RunAllCommand.CanExecute(null));
        Assert.IsTrue(sut.BuildsGroup.Rows.All(candidate => candidate.RunCommand.CanExecute(null)));
        Assert.IsFalse(row.HasComparison);

        await row.RunCommand.ExecuteAsync(null);

        Assert.IsTrue(row.IsDone);
        Assert.AreEqual("8.00 s", row.SystemValueText);
        Assert.AreEqual(string.Empty, row.DevValueText);
        Assert.IsFalse(row.ShowComparisonResult);
        await workload.Received(1).RunSingleSystemDriveAsync(
            "dotnet", @"C:\", 3,
            Arg.Any<IProgress<WorkloadRunProgress>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task InstalledToolProbe_DisablesOnlyUnavailableWorkloads()
    {
        IWorkloadBenchmarkService workload = Substitute.For<IWorkloadBenchmarkService>();
        IInstalledToolDetector detector = Substitute.For<IInstalledToolDetector>();
        detector.Detect("git", Arg.Any<CancellationToken>())
            .Returns(new InstalledToolInfo { Name = "git", Found = true, Version = "2.53.0" });
        detector.Detect("npm", Arg.Any<CancellationToken>())
            .Returns(new InstalledToolInfo { Name = "npm", Found = false });
        detector.Detect("dotnet", Arg.Any<CancellationToken>())
            .Returns(new InstalledToolInfo { Name = "dotnet", Found = true, Version = "10.0.0" });
        detector.Detect("cargo", Arg.Any<CancellationToken>())
            .Returns(new InstalledToolInfo { Name = "cargo", Found = false });
        var sut = new PerformanceSuiteViewModel(workload, detector);

        sut.Initialize(@"C:\", devRoot: null, devLetter: null, systemLetter: 'C');
        await sut.ToolDetectionTask;

        Assert.IsTrue(sut.BuildsGroup.Rows.Single(row => row.RequiredTool == "git").RunCommand.CanExecute(null));
        Assert.IsFalse(sut.BuildsGroup.Rows.Single(row => row.RequiredTool == "npm").RunCommand.CanExecute(null));
        Assert.IsTrue(sut.BuildsGroup.Rows.Single(row => row.RequiredTool == "dotnet").RunCommand.CanExecute(null));
        Assert.IsFalse(sut.BuildsGroup.Rows.Single(row => row.RequiredTool == "cargo").RunCommand.CanExecute(null));
        Assert.IsTrue(sut.RunAllCommand.CanExecute(null), "Run all remains useful when at least one workload is installed.");
        StringAssert.Contains(
            sut.BuildsGroup.Rows.Single(row => row.RequiredTool == "npm").ToolAvailabilityText,
            "not installed");
    }

    [TestMethod]
    public async Task InstalledToolProbe_DisablesRunAllWhenNoWorkloadCanRun()
    {
        IWorkloadBenchmarkService workload = Substitute.For<IWorkloadBenchmarkService>();
        IInstalledToolDetector detector = Substitute.For<IInstalledToolDetector>();
        detector.Detect(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new InstalledToolInfo { Name = call.ArgAt<string>(0), Found = false });
        var sut = new PerformanceSuiteViewModel(workload, detector);

        sut.Initialize(@"C:\", devRoot: null, devLetter: null, systemLetter: 'C');
        await sut.ToolDetectionTask;

        Assert.IsFalse(sut.RunAllCommand.CanExecute(null));
        Assert.IsTrue(sut.BuildsGroup.Rows.All(row => !row.RunCommand.CanExecute(null)));
        Assert.IsTrue(sut.BuildsGroup.Rows.All(row => row.ShowToolAvailability));
    }

    [TestMethod]
    public async Task InstalledToolProbe_PublishesRowChangesThroughUiDispatcher()
    {
        IWorkloadBenchmarkService workload = Substitute.For<IWorkloadBenchmarkService>();
        IInstalledToolDetector detector = Substitute.For<IInstalledToolDetector>();
        detector.Detect(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new InstalledToolInfo { Name = call.ArgAt<string>(0), Found = true });
        var pendingUiUpdates = new Queue<Action>();
        var sut = new PerformanceSuiteViewModel(
            workload,
            detector,
            action =>
            {
                pendingUiUpdates.Enqueue(action);
                return true;
            });

        sut.Initialize(@"C:\", devRoot: null, devLetter: null, systemLetter: 'C');
        await sut.ToolDetectionTask;

        Assert.HasCount(1, pendingUiUpdates);
        Assert.IsTrue(sut.BuildsGroup.Rows.All(row => !row.RunCommand.CanExecute(null)));

        pendingUiUpdates.Dequeue()();

        Assert.IsTrue(sut.BuildsGroup.Rows.All(row => row.RunCommand.CanExecute(null)));
        Assert.IsTrue(sut.RunAllCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task Reconfiguration_ClearsOldHeadlineAndPublishesNewRows()
    {
        PerformanceSuiteViewModel sut = CreateSut(out IWorkloadBenchmarkService workload);
        StubSingle(workload, new WorkloadMetric
        {
            Name = "dotnet build",
            SystemSeconds = 8d,
            DevSeconds = 4d,
        });
        await sut.BuildsGroup.Rows.Single(r => r.Id == "dotnet-build").RunCommand.ExecuteAsync(null);
        Assert.IsTrue(sut.HasHeadline);
        int configurationChanges = 0;
        sut.ConfigurationChanged += (_, _) => configurationChanges++;

        sut.Initialize(@"C:\", devRoot: null, devLetter: null, systemLetter: 'C');

        Assert.IsFalse(sut.HasHeadline);
        Assert.AreEqual(string.Empty, sut.HeadlineValue);
        Assert.AreEqual(1, configurationChanges);
        Assert.IsTrue(sut.BuildsGroup.Rows.All(row => !row.HasComparison));
    }

    [TestMethod]
    public void Construction_SeedsRequiredToolOnEveryBuildRow()
    {
        // The per-row Run dispatches to IWorkloadBenchmarkService.RunSingleAsync by the row's RequiredTool,
        // so every seeded build row must carry the tool it benchmarks.
        PerformanceSuiteViewModel sut = CreateSut(out _);

        Assert.AreEqual("git", sut.BuildsGroup.Rows.Single(r => r.Id == "git-clone").RequiredTool);
        Assert.AreEqual("npm", sut.BuildsGroup.Rows.Single(r => r.Id == "npm-ci").RequiredTool);
        Assert.AreEqual("dotnet", sut.BuildsGroup.Rows.Single(r => r.Id == "dotnet-build").RequiredTool);
        Assert.AreEqual("cargo", sut.BuildsGroup.Rows.Single(r => r.Id == "cargo-build").RequiredTool);
    }

    [TestMethod]
    public void Construction_SurfacesDiskSpeedAbsenceNote()
    {
        // The removed raw disk-I/O group leaves one calm, honest note so users don't wonder where it went:
        // raw throughput is ~1.0x on a shared physical disk; the real win is async AV scanning on real builds.
        PerformanceSuiteViewModel sut = CreateSut(out _);

        Assert.IsFalse(string.IsNullOrWhiteSpace(sut.DiskSpeedAbsenceNote));
        StringAssert.Contains(sut.DiskSpeedAbsenceNote, "Raw disk speed");
        StringAssert.Contains(sut.DiskSpeedAbsenceNote, "throughput");
        // The subtitle no longer advertises raw disk I/O.
        Assert.IsFalse(sut.Subtitle.Contains("disk I/O", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Construction_SurfacesHonestyLineAndUpsideCopy()
    {
        // CHANGE 1: the suite-level honesty line + the "You could do more" upside levers are present,
        // non-empty, and honest (the caveat names the perf-mode-off / group-policy reality).
        PerformanceSuiteViewModel sut = CreateSut(out _);

        StringAssert.Contains(sut.SuiteHonestyLine, "temp");
        StringAssert.Contains(sut.SuiteHonestyLine, "delete");
        Assert.IsFalse(string.IsNullOrWhiteSpace(sut.UpsideHeader));
        Assert.IsFalse(string.IsNullOrWhiteSpace(sut.UpsideIntro));
        Assert.IsFalse(string.IsNullOrWhiteSpace(sut.UpsideCachesLever));
        Assert.IsFalse(string.IsNullOrWhiteSpace(sut.UpsideSourceLever));
        Assert.IsFalse(string.IsNullOrWhiteSpace(sut.UpsidePerfModeLever));
        StringAssert.Contains(sut.UpsidePerfModeCaveat, "performance mode");
    }

    [TestMethod]
    public void UpsideHeader_IsSuggestions()
    {
        // CHANGE B: the relocated upside panel header is renamed "You could do more" -> "Suggestions".
        PerformanceSuiteViewModel sut = CreateSut(out _);

        Assert.AreEqual("Suggestions", sut.UpsideHeader);
        Assert.IsFalse(string.IsNullOrWhiteSpace(sut.UpsidePerfModeManagedNote));
        StringAssert.Contains(sut.UpsidePerfModeManagedNote, "managed by your organization");
    }

    [TestMethod]
    public void ApplyPerformanceMode_Off_ShowsLeverCaveatAndCaption()
    {
        // Genuinely off + not managed => the turn-on lever, the perf-mode-off caveat, and the demoted
        // Performance-section caption are all shown; the managed note is hidden.
        PerformanceSuiteViewModel sut = CreateSut(out _);

        sut.ApplyPerformanceMode(new EffectivePerformanceMode { State = PerformanceModeEffectiveness.Off });

        Assert.IsTrue(sut.ShowTurnOnPerfModeLever);
        Assert.IsTrue(sut.ShowPerfModeOffCaveat);
        Assert.IsTrue(sut.ShowPerformanceModeCaption);
        Assert.IsFalse(sut.ShowPerfModeManagedNote);
    }

    [TestMethod]
    public void ApplyPerformanceMode_Managed_ShowsManagedNoteHidesLeverAndCaveat()
    {
        // This machine's case: managed by the organization => never offer "turn on" or the off-caveat;
        // show the honest managed note instead.
        PerformanceSuiteViewModel sut = CreateSut(out _);

        sut.ApplyPerformanceMode(new EffectivePerformanceMode { State = PerformanceModeEffectiveness.Managed });

        Assert.IsFalse(sut.ShowTurnOnPerfModeLever, "Managed => no 'turn on performance mode' lever.");
        Assert.IsFalse(sut.ShowPerfModeOffCaveat, "Managed => no '~1.0x because off' caveat.");
        Assert.IsFalse(sut.ShowPerformanceModeCaption, "Managed => no demoted 'off' caption.");
        Assert.IsTrue(sut.ShowPerfModeManagedNote);
    }

    [TestMethod]
    public void ApplyPerformanceMode_OffButPolicyEnforced_ShowsManagedNoteNotLever()
    {
        // Policy-managed AND off: the user can't flip it locally, so suppress every actionable affordance
        // (lever, off caveat, and demoted caption) and show ONLY the managed note. Rendering the caveat or
        // caption alongside "nothing to turn on here" would be contradictory.
        PerformanceSuiteViewModel sut = CreateSut(out _);

        sut.ApplyPerformanceMode(new EffectivePerformanceMode
        {
            State = PerformanceModeEffectiveness.Off,
            PolicyEnforced = true,
        });

        Assert.IsFalse(sut.ShowTurnOnPerfModeLever, "Policy-managed => no local 'turn on' lever even when off.");
        Assert.IsTrue(sut.ShowPerfModeManagedNote, "Off + policy-enforced => show the managed note.");
        Assert.IsFalse(sut.ShowPerfModeOffCaveat, "C2: suppress the off caveat when policy-managed (managed note only).");
        Assert.IsFalse(sut.ShowPerformanceModeCaption, "C2: suppress the demoted caption when policy-managed (managed note only).");
    }

    [TestMethod]
    public void ApplyPerformanceMode_OnAndPolicyEnforced_HidesEverything_ThisMachine()
    {
        // THIS MACHINE: On (async) + managed by the org => the Suggestions perf-mode UI stays fully hidden
        // (no false "Off", no "turn on", no managed note). Drive health carries the "On (async)" line.
        PerformanceSuiteViewModel sut = CreateSut(out _);

        sut.ApplyPerformanceMode(new EffectivePerformanceMode
        {
            State = PerformanceModeEffectiveness.On,
            PolicyEnforced = true,
        });

        Assert.IsFalse(sut.ShowTurnOnPerfModeLever);
        Assert.IsFalse(sut.ShowPerfModeOffCaveat);
        Assert.IsFalse(sut.ShowPerformanceModeCaption);
        Assert.IsFalse(sut.ShowPerfModeManagedNote);
    }

    [TestMethod]
    [DataRow(PerformanceModeEffectiveness.On)]
    [DataRow(PerformanceModeEffectiveness.Unknown)]
    public void ApplyPerformanceMode_OnOrUnknown_HidesEverything(PerformanceModeEffectiveness state)
    {
        // On (or Unknown, e.g. unelevated) => no off-state UI and no managed note. Critically, nothing
        // claims "Off" or offers "turn on".
        PerformanceSuiteViewModel sut = CreateSut(out _);

        sut.ApplyPerformanceMode(new EffectivePerformanceMode { State = state });

        Assert.IsFalse(sut.ShowTurnOnPerfModeLever);
        Assert.IsFalse(sut.ShowPerfModeOffCaveat);
        Assert.IsFalse(sut.ShowPerformanceModeCaption);
        Assert.IsFalse(sut.ShowPerfModeManagedNote);
    }

    [TestMethod]
    public void DismissPerformanceModeCaption_ClearsOffStateFlags()
    {
        PerformanceSuiteViewModel sut = CreateSut(out _);
        sut.ApplyPerformanceMode(new EffectivePerformanceMode { State = PerformanceModeEffectiveness.Off });

        sut.DismissPerformanceModeCaption();

        Assert.IsFalse(sut.ShowPerformanceModeCaption);
        Assert.IsFalse(sut.ShowTurnOnPerfModeLever);
        Assert.IsFalse(sut.ShowPerfModeOffCaveat);
    }

    [TestMethod]
    public void Construction_EverySeededRow_HasPlainLanguageSubAndDetails()
    {
        // CHANGE 1a: every build row ships a concrete plain-language subtitle AND full Details copy
        // (what / exact command / methodology) so the suite is transparent, not gibberish.
        PerformanceSuiteViewModel sut = CreateSut(out _);

        foreach (PerfSuiteRowViewModel row in sut.BuildsGroup.Rows)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.Sub), $"{row.RowAutomationId} has no subtitle");
            Assert.IsTrue(row.HasDetails, $"{row.RowAutomationId} has no Details");
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.DetailsWhat), $"{row.RowAutomationId} has no DetailsWhat");
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.DetailsCommand), $"{row.RowAutomationId} has no DetailsCommand");
            Assert.IsFalse(string.IsNullOrWhiteSpace(row.DetailsMethodology), $"{row.RowAutomationId} has no DetailsMethodology");
        }

        // The build-row methodology must state the cold-first-build / first-discarded / median-of-2 facts.
        PerfSuiteRowViewModel npm = sut.BuildsGroup.Rows.Single(r => r.RowAutomationId == "TestRow_npm-ci");
        StringAssert.Contains(npm.DetailsCommand, "npm ci");
        StringAssert.Contains(npm.DetailsMethodology, "median");
    }

    [TestMethod]
    public async Task RunRow_RunsOnlyThatRow_GoesDone_AndOthersStayQueued()
    {
        // The per-row Run streams ONE benchmark via RunSingleAsync(requiredTool, …): the targeted row walks
        // Queued -> Running -> Done, the others are left untouched (still Queued), and the headline reflects
        // only that single row (total = 1), never averaged against rows that didn't run.
        PerformanceSuiteViewModel sut = CreateSut(out IWorkloadBenchmarkService workload);
        StubSingle(workload, new WorkloadMetric { Name = "dotnet build", SystemSeconds = 10d, DevSeconds = 5d });

        PerfSuiteRowViewModel target = sut.BuildsGroup.Rows.Single(r => r.Id == "dotnet-build");
        PerfSuiteRowViewModel other = sut.BuildsGroup.Rows.Single(r => r.Id == "git-clone");

        await target.RunCommand.ExecuteAsync(null);

        Assert.IsTrue(target.IsDone, "the targeted row should finish in the Done state");
        Assert.IsFalse(target.IsRunning);
        Assert.IsTrue(other.IsQueued, "other rows must stay Queued during a single-row run");
        Assert.IsFalse(sut.IsRunning, "the run gate must be released afterwards");
        Assert.IsTrue(sut.RunAllCommand.CanExecute(null));

        // Honest single-row headline: only this row counts (total = 1).
        Assert.IsTrue(sut.HasHeadline);
        Assert.AreEqual(1, sut.RunProgressMax);

        // The single-row engine was hit with the row's RequiredTool; the whole-comparison engine never ran.
        await workload.Received(1).RunSingleAsync(
            "dotnet", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<char>(),
            Arg.Any<int>(), Arg.Any<IProgress<WorkloadRunProgress>?>(), Arg.Any<CancellationToken>());
        await workload.DidNotReceive().RunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<char>(),
            Arg.Any<IProgress<WorkloadMetric>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task RunRow_AppliesSkip_WhenTheMetricIsSkipped()
    {
        // A skipped metric (missing tool / no network / low disk) lands the row in the Skipped state with a
        // reason rather than throwing — and still dispatches by the row's RequiredTool.
        PerformanceSuiteViewModel sut = CreateSut(out IWorkloadBenchmarkService workload);
        StubSingle(workload, new WorkloadMetric { Name = "cargo build", Skipped = true, SkipReason = "cargo is not installed" });

        PerfSuiteRowViewModel target = sut.BuildsGroup.Rows.Single(r => r.Id == "cargo-build");

        await target.RunCommand.ExecuteAsync(null);

        Assert.IsTrue(target.IsSkipped);
        Assert.IsFalse(target.IsDone);
        StringAssert.Contains(target.SkipReasonText, "cargo is not installed");
        Assert.IsFalse(sut.IsRunning);
        await workload.Received(1).RunSingleAsync(
            "cargo", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<char>(),
            Arg.Any<int>(), Arg.Any<IProgress<WorkloadRunProgress>?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task RunRow_SecondRunIsBlocked_WhileAlreadyRunning()
    {
        // The per-row Run shares the suite's IsRunning gate: while one row is in flight, a second Run (any
        // row, or "Run tests") is a no-op — it never reaches the engine and leaves the other row Queued.
        PerformanceSuiteViewModel sut = CreateSut(out IWorkloadBenchmarkService workload);
        var gate = new TaskCompletionSource<WorkloadMetric>();
        workload.RunSingleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<char>(),
            Arg.Any<int>(), Arg.Any<IProgress<WorkloadRunProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(gate.Task);

        PerfSuiteRowViewModel first = sut.BuildsGroup.Rows.Single(r => r.Id == "dotnet-build");
        PerfSuiteRowViewModel second = sut.BuildsGroup.Rows.Single(r => r.Id == "git-clone");

        Task firstRun = first.RunCommand.ExecuteAsync(null); // starts and parks on the gate
        Assert.IsTrue(sut.IsRunning);
        Assert.IsTrue(first.IsRunning);

        await second.RunCommand.ExecuteAsync(null); // blocked while a run is in progress
        Assert.IsTrue(second.IsQueued, "a second run must be blocked while one is already in progress");

        // Only the first row's run reached the engine.
        await workload.Received(1).RunSingleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<char>(),
            Arg.Any<int>(), Arg.Any<IProgress<WorkloadRunProgress>?>(), Arg.Any<CancellationToken>());

        gate.SetResult(new WorkloadMetric { Name = "dotnet build", SystemSeconds = 8d, DevSeconds = 4d });
        await firstRun;

        Assert.IsFalse(sut.IsRunning);
        Assert.IsTrue(first.IsDone);
    }

    [TestMethod]
    public async Task RunAll_RunsTheBuildEngine()
    {
        PerformanceSuiteViewModel sut = CreateSut(out IWorkloadBenchmarkService workload);

        await sut.RunAllCommand.ExecuteAsync(null);

        await workload.Received(1).RunAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<char>(),
            Arg.Any<IProgress<WorkloadMetric>?>(), Arg.Any<CancellationToken>());
        Assert.IsFalse(sut.IsRunning);
    }
}
