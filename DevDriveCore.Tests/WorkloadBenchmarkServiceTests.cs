using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;
using NSubstitute;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for the <see cref="WorkloadBenchmarkService"/> orchestrator. Every benchmark is a fake, so
/// the suite never launches a real process. Covers tool-gating, the per-drive iteration loop, the
/// median-of-speedups headline, incremental progress, skip-on-unavailable, and cleanup-always.
/// </summary>
[TestClass]
public sealed class WorkloadBenchmarkServiceTests
{
    private const string SystemRoot = "C:\\";
    private const string DevRoot = "G:\\";

    private static IInstalledToolDetector Detector(params (string Name, bool Found)[] tools)
    {
        var detector = Substitute.For<IInstalledToolDetector>();
        detector.DetectAll(Arg.Any<CancellationToken>())
            .Returns(tools.Select(t => new InstalledToolInfo { Name = t.Name, Found = t.Found }).ToList());

        // RunSingleAsync re-probes one tool via Detect(name): mirror the DetectAll result so a tool is
        // "found" only when it is in the list AND marked found; everything else is treated as missing.
        detector.Detect(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var name = (string)ci[0];
                bool found = tools.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) && t.Found);
                return new InstalledToolInfo { Name = name, Found = found };
            });

        return detector;
    }

    private static IPreflightProbe Preflight(PreflightInfo? info = null)
    {
        var probe = Substitute.For<IPreflightProbe>();
        probe.Capture(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<char>(), Arg.Any<CancellationToken>())
            .Returns(info ?? new PreflightInfo());
        return probe;
    }

    private static WorkloadBenchmarkService Service(
        IReadOnlyList<IWorkloadBenchmark> benchmarks,
        IInstalledToolDetector detector,
        IPreflightProbe? preflight = null,
        int iterations = 5,
        WorkloadProfile globalProfile = WorkloadProfile.Thorough)
    {
        DateTimeOffset now = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        return new WorkloadBenchmarkService(
            benchmarks,
            detector,
            preflight ?? Preflight(),
            new WorkloadBenchmarkOptions
            {
                Iterations = iterations,
                GlobalProfile = globalProfile,
            },
            () => now,
            () => "C:\\seed-not-used");
    }

    [TestMethod]
    public async Task RunAsync_AllToolsInstalled_MeasuresBothDrivesAndBuildsHeadline()
    {
        var git = new FakeWorkloadBenchmark("git clone", "git", SystemRoot, systemSeconds: 4d, devSeconds: 2d);   // 2.0x
        var cargo = new FakeWorkloadBenchmark("cargo build", "cargo", SystemRoot, systemSeconds: 9d, devSeconds: 3d); // 3.0x
        var service = Service(new IWorkloadBenchmark[] { git, cargo }, Detector(("git", true), ("cargo", true)));

        WorkloadComparison comparison = await service.RunAsync(SystemRoot, DevRoot, 'G');

        Assert.HasCount(2, comparison.Metrics);
        Assert.IsFalse(comparison.Metrics[0].Skipped);
        Assert.IsFalse(comparison.Metrics[1].Skipped);
        Assert.AreEqual(2.5d, comparison.HeadlineSpeedup, 1e-9); // median of {2.0, 3.0}

        // 5 system runs then 5 dev runs, on each benchmark.
        Assert.AreEqual(1, git.PrepareCount);
        Assert.AreEqual(1, git.CleanupCount);
        CollectionAssert.AreEqual(
            Enumerable.Repeat(SystemRoot, 5).Concat(Enumerable.Repeat(DevRoot, 5)).ToList(),
            git.MeasuredRoots);
    }

    [TestMethod]
    public async Task RunAsync_StampsRunModeNoteOntoPreflight()
    {
        var git = new FakeWorkloadBenchmark("git clone", "git");
        var service = Service(new IWorkloadBenchmark[] { git }, Detector(("git", true)), iterations: 5);

        WorkloadComparison comparison = await service.RunAsync(SystemRoot, DevRoot, 'G');

        Assert.AreEqual("Cold first build · 5 runs · first discarded · median", comparison.Preflight.RunModeNote);
    }

    [TestMethod]
    public async Task RunAsync_ToolMissing_SkipsWithReason_AndNeverPreparesOrCleans()
    {
        var git = new FakeWorkloadBenchmark("git clone", "git");
        var npm = new FakeWorkloadBenchmark("npm ci", "npm");
        var service = Service(new IWorkloadBenchmark[] { git, npm }, Detector(("git", true), ("npm", false)));

        WorkloadComparison comparison = await service.RunAsync(SystemRoot, DevRoot, 'G');

        WorkloadMetric npmRow = comparison.Metrics.Single(m => m.Name == "npm ci");
        Assert.IsTrue(npmRow.Skipped);
        Assert.AreEqual("npm is not installed", npmRow.SkipReason);
        Assert.AreEqual(0, npm.PrepareCount);
        Assert.AreEqual(0, npm.CleanupCount);
        Assert.IsEmpty(npm.MeasuredRoots);
    }

    [TestMethod]
    public async Task RunAsync_PrepareUnavailable_SkipsWithReason_AndStillCleansUp()
    {
        var git = new FakeWorkloadBenchmark("git clone", "git", prepareSkip: "no network to seed");
        var service = Service(new IWorkloadBenchmark[] { git }, Detector(("git", true)));

        WorkloadComparison comparison = await service.RunAsync(SystemRoot, DevRoot, 'G');

        WorkloadMetric row = comparison.Metrics.Single();
        Assert.IsTrue(row.Skipped);
        Assert.AreEqual("no network to seed", row.SkipReason);
        Assert.AreEqual(1, git.PrepareCount);
        Assert.AreEqual(1, git.CleanupCount);          // cleanup runs in finally even when Prepare skipped
        Assert.IsEmpty(git.MeasuredRoots);
    }

    [TestMethod]
    public async Task RunAsync_MeasureUnavailable_SkipsWithReason_AndStillCleansUp()
    {
        var git = new FakeWorkloadBenchmark("git clone", "git", measureSkip: "git clone failed (exit 128)");
        var service = Service(new IWorkloadBenchmark[] { git }, Detector(("git", true)));

        WorkloadComparison comparison = await service.RunAsync(SystemRoot, DevRoot, 'G');

        WorkloadMetric row = comparison.Metrics.Single();
        Assert.IsTrue(row.Skipped);
        Assert.AreEqual("git clone failed (exit 128)", row.SkipReason);
        Assert.AreEqual(1, git.CleanupCount);
    }

    [TestMethod]
    public async Task RunAsync_UnexpectedException_IsContainedAsSkip_AndRunContinues()
    {
        var git = new FakeWorkloadBenchmark("git clone", "git", prepareThrows: new InvalidOperationException("boom"));
        var cargo = new FakeWorkloadBenchmark("cargo build", "cargo", SystemRoot, systemSeconds: 6d, devSeconds: 2d); // 3.0x
        var service = Service(new IWorkloadBenchmark[] { git, cargo }, Detector(("git", true), ("cargo", true)));

        WorkloadComparison comparison = await service.RunAsync(SystemRoot, DevRoot, 'G');

        WorkloadMetric gitRow = comparison.Metrics.Single(m => m.Name == "git clone");
        Assert.IsTrue(gitRow.Skipped);
        StringAssert.Contains(gitRow.SkipReason, "boom");
        Assert.AreEqual(1, git.CleanupCount);

        // The run continues; cargo still measured and drives the headline.
        Assert.IsFalse(comparison.Metrics.Single(m => m.Name == "cargo build").Skipped);
        Assert.AreEqual(3d, comparison.HeadlineSpeedup, 1e-9);
    }

    [TestMethod]
    public async Task RunAsync_ReportsEachRowOnce_InOrder()
    {
        var git = new FakeWorkloadBenchmark("git clone", "git");
        var cargo = new FakeWorkloadBenchmark("cargo build", "cargo");
        var service = Service(new IWorkloadBenchmark[] { git, cargo }, Detector(("git", true), ("cargo", true)));
        var progress = new RecordingProgress<WorkloadMetric>();

        await service.RunAsync(SystemRoot, DevRoot, 'G', progress);

        CollectionAssert.AreEqual(new[] { "git clone", "cargo build" }, progress.Reports.Select(r => r.Name).ToList());
    }

    [TestMethod]
    public async Task RunAsync_RespectsIterationCount()
    {
        var git = new FakeWorkloadBenchmark("git clone", "git");
        var service = Service(new IWorkloadBenchmark[] { git }, Detector(("git", true)), iterations: 3);

        await service.RunAsync(SystemRoot, DevRoot, 'G');

        CollectionAssert.AreEqual(
            new[] { SystemRoot, SystemRoot, SystemRoot, DevRoot, DevRoot, DevRoot },
            git.MeasuredRoots);
    }

    [TestMethod]
    public async Task RunAsync_PopulatesPhaseTimings_BuildSecondsSumsEveryRun()
    {
        var git = new FakeWorkloadBenchmark("git clone", "git", SystemRoot, systemSeconds: 4d, devSeconds: 2d);
        var service = Service(new IWorkloadBenchmark[] { git }, Detector(("git", true)), iterations: 3);

        WorkloadComparison comparison = await service.RunAsync(SystemRoot, DevRoot, 'G');
        WorkloadMetric m = comparison.Metrics[0];

        // 3 system runs (4d) + 3 dev runs (2d) = 18s of measured builds, across both drives.
        Assert.AreEqual(18d, m.BuildSeconds, 1e-9);
        Assert.IsGreaterThanOrEqualTo(0d, m.PrepareSeconds);
        Assert.IsGreaterThanOrEqualTo(0d, m.SetupSeconds);
        Assert.IsGreaterThanOrEqualTo(m.BuildSeconds, m.TotalSeconds); // total >= just the builds
    }

    [TestMethod]
    public async Task RunAsync_BlankRoots_Throw()
    {
        var service = Service(Array.Empty<IWorkloadBenchmark>(), Detector());

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.RunAsync(" ", DevRoot, 'G'));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.RunAsync(SystemRoot, " ", 'G'));
    }

    // ----- RunSingleAsync (inline per-cache "Test speed") -------------------------------------------

    [TestMethod]
    public async Task RunSingleAsync_RunsOnlyMatchingBenchmark_AndMeasuresBothDrives()
    {
        var git = new FakeWorkloadBenchmark("git clone", "git", SystemRoot, systemSeconds: 4d, devSeconds: 2d);
        var cargo = new FakeWorkloadBenchmark("cargo build", "cargo", SystemRoot, systemSeconds: 9d, devSeconds: 3d); // 3.0x
        var service = Service(new IWorkloadBenchmark[] { git, cargo }, Detector(("git", true), ("cargo", true)));

        WorkloadMetric metric = await service.RunSingleAsync("cargo", SystemRoot, DevRoot, 'G', iterations: 3);

        Assert.AreEqual("cargo build", metric.Name);
        Assert.IsFalse(metric.Skipped);
        Assert.AreEqual(3d, metric.Speedup, 1e-9);

        // Only the cargo benchmark ran: 3 system + 3 dev measurements; git untouched.
        Assert.AreEqual(1, cargo.PrepareCount);
        Assert.AreEqual(1, cargo.CleanupCount);
        CollectionAssert.AreEqual(
            new[] { SystemRoot, SystemRoot, SystemRoot, DevRoot, DevRoot, DevRoot },
            cargo.MeasuredRoots);
        Assert.AreEqual(0, git.PrepareCount);
        Assert.IsEmpty(git.MeasuredRoots);
    }

    [TestMethod]
    public async Task RunSingleAsync_MatchesRequiredTool_CaseInsensitively()
    {
        var cargo = new FakeWorkloadBenchmark("cargo build", "cargo", SystemRoot, systemSeconds: 6d, devSeconds: 2d);
        var service = Service(new IWorkloadBenchmark[] { cargo }, Detector(("cargo", true)));

        WorkloadMetric metric = await service.RunSingleAsync("CARGO", SystemRoot, DevRoot, 'G', iterations: 3);

        Assert.AreEqual("cargo build", metric.Name);
        Assert.IsFalse(metric.Skipped);
    }

    [TestMethod]
    public async Task RunSingleAsync_NoBenchmarkForTool_SkipsWithReason_AndRunsNothing()
    {
        var git = new FakeWorkloadBenchmark("git clone", "git");
        // Tool is even reported installed, but there is no benchmark whose RequiredTool == "pip".
        var service = Service(new IWorkloadBenchmark[] { git }, Detector(("pip", true), ("git", true)));

        WorkloadMetric metric = await service.RunSingleAsync("pip", SystemRoot, DevRoot, 'G', iterations: 3);

        Assert.IsTrue(metric.Skipped);
        Assert.AreEqual("no workload test is available for pip", metric.SkipReason);
        Assert.AreEqual(0, git.PrepareCount);
        Assert.IsEmpty(git.MeasuredRoots);
    }

    [TestMethod]
    public async Task RunSingleAsync_ToolNotInstalled_SkipsWithReason_AndNeverPreparesOrCleans()
    {
        var cargo = new FakeWorkloadBenchmark("cargo build", "cargo");
        var service = Service(new IWorkloadBenchmark[] { cargo }, Detector(("cargo", false)));

        WorkloadMetric metric = await service.RunSingleAsync("cargo", SystemRoot, DevRoot, 'G', iterations: 3);

        Assert.IsTrue(metric.Skipped);
        Assert.AreEqual("cargo is not installed", metric.SkipReason);
        Assert.AreEqual(0, cargo.PrepareCount);
        Assert.AreEqual(0, cargo.CleanupCount);
        Assert.IsEmpty(cargo.MeasuredRoots);
    }

    [TestMethod]
    public async Task RunSingleAsync_MeasureUnavailable_SkipsWithReason_AndStillCleansUp()
    {
        var cargo = new FakeWorkloadBenchmark("cargo build", "cargo", measureSkip: "cargo build failed (exit 101)");
        var service = Service(new IWorkloadBenchmark[] { cargo }, Detector(("cargo", true)));

        WorkloadMetric metric = await service.RunSingleAsync("cargo", SystemRoot, DevRoot, 'G', iterations: 3);

        Assert.IsTrue(metric.Skipped);
        Assert.AreEqual("cargo build failed (exit 101)", metric.SkipReason);
        Assert.AreEqual(1, cargo.CleanupCount);          // cleanup runs in finally
    }

    [TestMethod]
    public async Task RunSingleAsync_ReportsProgress_PreparingThenSystemRunsThenDevRuns()
    {
        var cargo = new FakeWorkloadBenchmark("cargo build", "cargo", SystemRoot, systemSeconds: 6d, devSeconds: 2d);
        var service = Service(new IWorkloadBenchmark[] { cargo }, Detector(("cargo", true)));
        var progress = new RecordingProgress<WorkloadRunProgress>();

        await service.RunSingleAsync("cargo", SystemRoot, DevRoot, 'G', iterations: 3, progress: progress);

        IReadOnlyList<WorkloadRunProgress> reports = progress.Reports;
        Assert.HasCount(7, reports); // Preparing + 3 system + 3 dev

        Assert.AreEqual(WorkloadRunStage.Preparing, reports[0].Stage);
        Assert.AreEqual(0, reports[0].Run);

        CollectionAssert.AreEqual(
            new[]
            {
                (WorkloadRunStage.SystemDrive, 1), (WorkloadRunStage.SystemDrive, 2), (WorkloadRunStage.SystemDrive, 3),
                (WorkloadRunStage.DevDrive, 1), (WorkloadRunStage.DevDrive, 2), (WorkloadRunStage.DevDrive, 3),
            },
            reports.Skip(1).Select(r => (r.Stage, r.Run)).ToList());

        Assert.IsTrue(reports.All(r => r.TotalRuns == 3));
    }

    [TestMethod]
    public async Task RunSingleAsync_IterationsZero_UsesConfiguredDefault()
    {
        var cargo = new FakeWorkloadBenchmark("cargo build", "cargo");
        var service = Service(new IWorkloadBenchmark[] { cargo }, Detector(("cargo", true)), iterations: 5);

        await service.RunSingleAsync("cargo", SystemRoot, DevRoot, 'G', iterations: 0);

        Assert.HasCount(10, cargo.MeasuredRoots); // 5 system + 5 dev
    }

    [TestMethod]
    public async Task RunSingleAsync_PreCancelledToken_PropagatesCancellation_NotSkip()
    {
        var cargo = new FakeWorkloadBenchmark("cargo build", "cargo");
        var service = Service(new IWorkloadBenchmark[] { cargo }, Detector(("cargo", true)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => service.RunSingleAsync("cargo", SystemRoot, DevRoot, 'G', iterations: 3, cancellationToken: cts.Token));
    }

    [TestMethod]
    public async Task RunSingleAsync_BlankArgs_Throw()
    {
        var service = Service(Array.Empty<IWorkloadBenchmark>(), Detector());

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.RunSingleAsync(" ", SystemRoot, DevRoot, 'G'));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.RunSingleAsync("cargo", " ", DevRoot, 'G'));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.RunSingleAsync("cargo", SystemRoot, " ", 'G'));
    }

    // ---- Profile selection: global card = Thorough, inline per-cache test = Quick -----------------

    [TestMethod]
    public async Task RunAsync_AppliesThoroughProfile_ToEachBenchmark_BeforePreparing()
    {
        var git = new FakeWorkloadBenchmark("git clone", "git");
        var npm = new FakeWorkloadBenchmark("npm ci", "npm");
        var service = Service(new IWorkloadBenchmark[] { git, npm }, Detector(("git", true), ("npm", true)));

        await service.RunAsync(SystemRoot, DevRoot, 'G');

        // The global card runs the realistic Thorough fixtures; the profile is applied before Prepare.
        Assert.AreEqual(WorkloadProfile.Thorough, git.ProfileAtPrepare);
        Assert.AreEqual(WorkloadProfile.Thorough, npm.ProfileAtPrepare);
    }

    [TestMethod]
    public async Task RunSingleAsync_AppliesGlobalProfile_BeforePreparing()
    {
        var cargo = new FakeWorkloadBenchmark("cargo build", "cargo");
        var service = Service(new IWorkloadBenchmark[] { cargo }, Detector(("cargo", true)));

        await service.RunSingleAsync("cargo", SystemRoot, DevRoot, 'G', iterations: 3);

        // F8: the per-row Run uses the SAME profile as the global "Run tests" (GlobalProfile, default
        // Thorough) so the generated fixture matches the row's displayed copy.
        Assert.AreEqual(WorkloadProfile.Thorough, cargo.ProfileAtPrepare);
    }

    [TestMethod]
    public async Task RunAsync_HonorsConfiguredGlobalProfile()
    {
        var git = new FakeWorkloadBenchmark("git clone", "git");
        var service = Service(
            new IWorkloadBenchmark[] { git }, Detector(("git", true)), globalProfile: WorkloadProfile.Quick);

        await service.RunAsync(SystemRoot, DevRoot, 'G');

        Assert.AreEqual(WorkloadProfile.Quick, git.ProfileAtPrepare);
    }

    [TestMethod]
    public async Task RunSingleAsync_HonorsConfiguredGlobalProfile_ForPerRowRun()
    {
        var cargo = new FakeWorkloadBenchmark("cargo build", "cargo");
        var service = Service(
            new IWorkloadBenchmark[] { cargo }, Detector(("cargo", true)), globalProfile: WorkloadProfile.Quick);

        await service.RunSingleAsync("cargo", SystemRoot, DevRoot, 'G', iterations: 3);

        // F8: per-row Run follows GlobalProfile — configure it Quick and the single run uses Quick too.
        Assert.AreEqual(WorkloadProfile.Quick, cargo.ProfileAtPrepare);
    }
}
