using System.Diagnostics;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;

namespace DevDriveCore.Services;

/// <summary>
/// Default <see cref="IWorkloadBenchmarkService"/>. Composes the installed-tool detector (gating), a
/// pre-flight probe (context), the set of <see cref="IWorkloadBenchmark"/>s (execution), and the pure
/// <see cref="WorkloadMath"/> (aggregation). All decision/aggregation logic lives here so it is
/// unit-testable against fakes — no real process is launched in tests.
/// </summary>
/// <remarks>
/// <para>Each benchmark runs N cold first-build iterations on the system drive then N on the Dev Drive
/// (sequential per drive, mirroring the synthetic <see cref="SpeedTestService"/>); every iteration
/// freshly re-populates its per-drive package cache, the first run is discarded, and the median is
/// reported.</para>
/// <para><see cref="RunSingleAsync"/> runs just one benchmark (by its required tool) with fine-grained
/// per-iteration progress — this backs the performance suite's per-row Run (each row's Run button
/// measures just that tool's cache, using the same fixture profile as the global "Run tests").</para>
/// <para><b>SAFETY:</b> every benchmark's <c>Cleanup</c> runs in a <c>finally</c>, so bounded temp
/// fixtures are always removed even if a measurement throws or the run is cancelled.</para>
/// </remarks>
public sealed class WorkloadBenchmarkService : IWorkloadBenchmarkService
{
    private readonly IReadOnlyList<IWorkloadBenchmark> _benchmarks;
    private readonly IInstalledToolDetector _detector;
    private readonly IPreflightProbe _preflight;
    private readonly WorkloadBenchmarkOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string> _seedRootFactory;

    /// <summary>Creates a service over the supplied benchmarks, detector, pre-flight probe, and tunables.</summary>
    public WorkloadBenchmarkService(
        IReadOnlyList<IWorkloadBenchmark> benchmarks,
        IInstalledToolDetector detector,
        IPreflightProbe preflight,
        WorkloadBenchmarkOptions? options = null,
        Func<DateTimeOffset>? now = null,
        Func<string>? seedRootFactory = null)
    {
        _benchmarks = benchmarks ?? throw new ArgumentNullException(nameof(benchmarks));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _options = options ?? WorkloadBenchmarkOptions.Default;
        _now = now ?? (() => DateTimeOffset.Now);
        _seedRootFactory = seedRootFactory
            ?? (() => Path.Combine(Path.GetTempPath(), WorkloadPaths.SeedFolderName));
    }

    /// <summary>Convenience factory wiring the real process runner, tool detector, pre-flight probe, and benchmarks.</summary>
    public static WorkloadBenchmarkService CreateDefault(IInstalledToolDetector? detector = null)
    {
        var runner = new WorkloadProcessRunner();
        return new WorkloadBenchmarkService(
            WorkloadBenchmarkCatalog.CreateDefault(runner),
            detector ?? InstalledToolDetector.CreateDefault(),
            PreflightProbe.CreateDefault());
    }

    /// <inheritdoc />
    public async Task<WorkloadComparison> RunAsync(
        string systemDriveRoot,
        string devDriveRoot,
        char devDriveLetter,
        IProgress<WorkloadMetric>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDriveRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(devDriveRoot);

        return await Task.Run(
            () => Run(systemDriveRoot, devDriveRoot, devDriveLetter, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkloadComparison> RunSystemDriveAsync(
        string systemDriveRoot,
        IProgress<WorkloadMetric>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDriveRoot);

        return await Task.Run(
            () => Run(
                systemDriveRoot,
                devDriveRoot: null,
                devDriveLetter: null,
                progress: progress,
                cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkloadMetric> RunSingleAsync(
        string requiredTool,
        string systemDriveRoot,
        string devDriveRoot,
        char devDriveLetter,
        int iterations = 0,
        IProgress<WorkloadRunProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredTool);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDriveRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(devDriveRoot);

        return await Task.Run(
            () => RunSingle(requiredTool, systemDriveRoot, devDriveRoot, iterations, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkloadMetric> RunSingleSystemDriveAsync(
        string requiredTool,
        string systemDriveRoot,
        int iterations = 0,
        IProgress<WorkloadRunProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredTool);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDriveRoot);

        return await Task.Run(
            () => RunSingle(
                requiredTool,
                systemDriveRoot,
                devDriveRoot: null,
                iterations: iterations,
                progress: progress,
                cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private WorkloadComparison Run(
        string systemDriveRoot,
        string? devDriveRoot,
        char? devDriveLetter,
        IProgress<WorkloadMetric>? progress,
        CancellationToken cancellationToken)
    {
        int iterations = Math.Max(1, _options.Iterations);

        PreflightInfo preflight = devDriveRoot is not null && devDriveLetter is char letter
            ? _preflight.Capture(systemDriveRoot, devDriveRoot, letter, cancellationToken)
            : _preflight.CaptureSystemDrive(systemDriveRoot, cancellationToken);
        preflight = preflight
            with { RunModeNote = DescribeRunMode(iterations) };

        // Detect installed tools once, then gate each benchmark on its required tool.
        var installed = new HashSet<string>(
            _detector.DetectAll(cancellationToken).Where(t => t.Found).Select(t => t.Name),
            StringComparer.OrdinalIgnoreCase);

        string seedRoot = _seedRootFactory();
        var environment = new WorkloadEnvironment(systemDriveRoot, devDriveRoot ?? systemDriveRoot, seedRoot);

        var rows = new List<WorkloadMetric>(_benchmarks.Count);
        foreach (IWorkloadBenchmark benchmark in _benchmarks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Global card runs the realistic, real-world-app fixtures. Set the profile BEFORE reading
            // Detail (used for the uninstalled-tool skip row below) or running.
            benchmark.Profile = _options.GlobalProfile;

            WorkloadMetric row = installed.Contains(benchmark.RequiredTool)
                ? RunBenchmark(benchmark, environment, systemDriveRoot, devDriveRoot, iterations, progress: null, cancellationToken)
                : WorkloadMath.Skipped(benchmark.Name, benchmark.Detail, $"{benchmark.RequiredTool} is not installed");

            rows.Add(row);
            progress?.Report(row);
        }

        return WorkloadMath.Build(rows, _now(), preflight);
    }

    private WorkloadMetric RunSingle(
        string requiredTool,
        string systemDriveRoot,
        string? devDriveRoot,
        int iterations,
        IProgress<WorkloadRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        int iters = Math.Max(1, iterations > 0 ? iterations : _options.Iterations);

        IWorkloadBenchmark? benchmark = _benchmarks.FirstOrDefault(
            b => string.Equals(b.RequiredTool, requiredTool, StringComparison.OrdinalIgnoreCase));
        if (benchmark is null)
        {
            // No benchmark for this cache's tool — surface as a skip rather than throwing.
            return WorkloadMath.Skipped(requiredTool, string.Empty, $"no workload test is available for {requiredTool}");
        }

        // The per-row Run uses the SAME fixture profile as the global "Run tests" (GlobalProfile,
        // default Thorough), so a row's generated fixture matches its displayed copy (e.g. the git
        // row's "~15,000-file repository"). Set the profile BEFORE reading Detail (used for the skip
        // rows below) or running.
        benchmark.Profile = _options.GlobalProfile;

        // Defensive gate (the UI also gates on installed): re-probe just this one tool.
        InstalledToolInfo tool = _detector.Detect(requiredTool, cancellationToken);
        if (!tool.Found)
        {
            return WorkloadMath.Skipped(benchmark.Name, benchmark.Detail, $"{requiredTool} is not installed");
        }

        string seedRoot = _seedRootFactory();
        var environment = new WorkloadEnvironment(systemDriveRoot, devDriveRoot ?? systemDriveRoot, seedRoot);

        return RunBenchmark(benchmark, environment, systemDriveRoot, devDriveRoot, iters, progress, cancellationToken);
    }

    private static WorkloadMetric RunBenchmark(
        IWorkloadBenchmark benchmark,
        WorkloadEnvironment environment,
        string systemDriveRoot,
        string? devDriveRoot,
        int iterations,
        IProgress<WorkloadRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report(new WorkloadRunProgress(WorkloadRunStage.Preparing, 0, iterations));
            var prepareStopwatch = Stopwatch.StartNew();
            benchmark.Prepare(environment, cancellationToken);
            prepareStopwatch.Stop();

            // Wrap each per-drive measurement so we can separate the UNTIMED per-iteration setup (the cold
            // cache re-copy + source copy) from the measured builds: setup = measured wall-clock − the build
            // times themselves. This is what makes a ~15s build take minutes — surfaced for transparency.
            var measureStopwatch = Stopwatch.StartNew();
            double[] systemRuns = MeasureDrive(benchmark, systemDriveRoot, iterations, WorkloadRunStage.SystemDrive, progress, cancellationToken);
            double[] devRuns = devDriveRoot is null
                ? Array.Empty<double>()
                : MeasureDrive(benchmark, devDriveRoot, iterations, WorkloadRunStage.DevDrive, progress, cancellationToken);
            measureStopwatch.Stop();

            double buildSeconds = systemRuns.Sum() + devRuns.Sum();
            double setupSeconds = measureStopwatch.Elapsed.TotalSeconds - buildSeconds; // cold-cache + source copy

            return WorkloadMath.BuildMetric(
                benchmark.Name, benchmark.Detail, systemRuns, devRuns,
                prepareStopwatch.Elapsed.TotalSeconds, setupSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WorkloadUnavailableException ex)
        {
            return WorkloadMath.Skipped(benchmark.Name, benchmark.Detail, ex.Message);
        }
        catch (Exception ex)
        {
            // Defensive: never let one misbehaving benchmark fail the whole comparison.
            return WorkloadMath.Skipped(benchmark.Name, benchmark.Detail, $"could not run: {ex.Message}");
        }
        finally
        {
            benchmark.Cleanup();
        }
    }

    private static double[] MeasureDrive(
        IWorkloadBenchmark benchmark,
        string driveRoot,
        int iterations,
        WorkloadRunStage stage,
        IProgress<WorkloadRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        var times = new double[iterations];
        for (int i = 0; i < iterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new WorkloadRunProgress(stage, i + 1, iterations));
            times[i] = benchmark.MeasureOnce(driveRoot, cancellationToken);
        }

        return times;
    }

    private static string DescribeRunMode(int iterations) =>
        iterations <= 1
            ? "Cold first build · single run"
            : $"Cold first build · {iterations} runs · first discarded · median";
}
