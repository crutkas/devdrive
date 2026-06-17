namespace DevDriveCore.Models;

/// <summary>
/// One row of the real-developer-workload comparison: the same operation (e.g. <c>git clone</c>)
/// timed on the system drive and the Dev Drive. Unlike <see cref="SpeedMetric"/> (which is
/// higher-is-better MB/s / IOPS), these are <b>wall-clock seconds</b> — <em>lower is better</em> — so
/// the win ratio is <see cref="Speedup"/> = system ÷ dev (a value above <c>1.0</c> means the Dev Drive
/// finished faster).
/// </summary>
/// <remarks>
/// A single record models both a measured row and a <see cref="Skipped"/> row (e.g. the tool isn't
/// installed, or there was no network to seed a fixture) so the UI can present "SKIPPED — &lt;reason&gt;"
/// rather than hiding a missing benchmark. Pure data — performs no work.
/// </remarks>
public sealed record WorkloadMetric
{
    /// <summary>Human-readable operation name, e.g. "git clone".</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Short description of the fixture / what was measured, e.g. "local bare repo · 1,200 files".</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>True when this benchmark did not run (tool missing, no network, low disk, or an error).</summary>
    public bool Skipped { get; init; }

    /// <summary>Why the benchmark was skipped (only meaningful when <see cref="Skipped"/> is true).</summary>
    public string? SkipReason { get; init; }

    /// <summary>Median wall-clock seconds on the system drive (the measured runs, after discarding the first).</summary>
    public double SystemSeconds { get; init; }

    /// <summary>Median wall-clock seconds on the Dev Drive (the measured runs, after discarding the first).</summary>
    public double DevSeconds { get; init; }

    /// <summary>Every system-drive run time (including the discarded first run) — surfaced for transparency.</summary>
    public IReadOnlyList<double> SystemRuns { get; init; } = Array.Empty<double>();

    /// <summary>Every Dev Drive run time (including the discarded first run) — surfaced for transparency.</summary>
    public IReadOnlyList<double> DevRuns { get; init; } = Array.Empty<double>();

    /// <summary>
    /// One-time setup seconds: cloning the fixture and warming its package cache over the network. This
    /// is NOT part of the measured (lower-is-better) build time, but it is real wall-clock the run spends.
    /// </summary>
    public double PrepareSeconds { get; init; }

    /// <summary>
    /// Total per-iteration setup seconds across ALL runs (both drives): re-copying the package cache cold
    /// onto the drive under test plus the source — the untimed-but-real overhead behind each cold build.
    /// </summary>
    public double SetupSeconds { get; init; }

    /// <summary>Total wall-clock seconds of the measured builds themselves — the sum of every run on both drives.</summary>
    public double BuildSeconds => SystemRuns.Sum() + DevRuns.Sum();

    /// <summary>Approximate total wall-clock the row took: <see cref="PrepareSeconds"/> + <see cref="SetupSeconds"/> + <see cref="BuildSeconds"/>.</summary>
    public double TotalSeconds => PrepareSeconds + SetupSeconds + BuildSeconds;

    /// <summary>
    /// Win ratio: system median ÷ Dev Drive median. Above <c>1.0</c> means the Dev Drive was faster.
    /// Returns <c>0</c> when skipped or either median is non-positive, so the headline math can ignore it.
    /// </summary>
    public double Speedup =>
        Skipped || SystemSeconds <= 0d || DevSeconds <= 0d ? 0d : SystemSeconds / DevSeconds;
}
