namespace DevDriveCore.Services;

/// <summary>
/// Accumulates the running headline for the Performance test suite as rows stream in. It tracks how many
/// of the planned <see cref="Total"/> rows have finished (a measured result <em>or</em> a skip) and the
/// geometric-mean speedup of the rows that produced a real measurement. Pure and deterministic, so the
/// "1.4× faster on average so far · k of N complete" headline is unit-testable without any benchmarking.
/// </summary>
public sealed class PerfSuiteAggregator
{
    private readonly List<double> _speedups = new();

    /// <param name="total">The number of rows the suite plans to run (disk + builds + installed caches).</param>
    public PerfSuiteAggregator(int total) => Total = Math.Max(0, total);

    /// <summary>Total rows the suite plans to run.</summary>
    public int Total { get; }

    /// <summary>Rows that produced a measurement (whether or not the speedup was computable).</summary>
    public int CompletedWithResult { get; private set; }

    /// <summary>Rows that were skipped (missing tool, no network, low disk, cancelled…).</summary>
    public int Skipped { get; private set; }

    /// <summary>Rows that have finished one way or the other — the "k" in "k of N complete".</summary>
    public int Finished => CompletedWithResult + Skipped;

    /// <summary>True once at least one row has produced a computable speedup.</summary>
    public bool HasResult => _speedups.Count > 0;

    /// <summary>Geometric mean of the recorded positive speedups (skips and non-computable rows are excluded).</summary>
    public double AverageSpeedup => SpeedTestMath.GeometricMean(_speedups);

    /// <summary>Records a finished measurement; <paramref name="speedup"/> &lt;= 0 still counts as completed but is excluded from the mean.</summary>
    public void AddResult(double speedup)
    {
        CompletedWithResult++;
        if (speedup > 0d && !double.IsNaN(speedup) && !double.IsInfinity(speedup))
        {
            _speedups.Add(speedup);
        }
    }

    /// <summary>Records a skipped row (counts toward <see cref="Finished"/>, excluded from the mean).</summary>
    public void AddSkip() => Skipped++;

    /// <summary>The big "1.4×" headline number (em dash until a result lands), e.g. <c>FormatSpeedup(AverageSpeedup)</c>.</summary>
    public string HeadlineValue => PerfSuiteMath.FormatSpeedup(AverageSpeedup);

    /// <summary>The caption beside the headline, e.g. "faster on average so far · 5 of 11 complete".</summary>
    public string HeadlineCaption =>
        HasResult
            ? $"faster on average so far \u00B7 {Finished} of {Total} complete"
            : $"{Finished} of {Total} complete";
}
