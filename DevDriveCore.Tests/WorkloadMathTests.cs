using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for the pure <see cref="WorkloadMath"/> (median, discard-first, per-operation rows, and the
/// median-of-speedups headline). No I/O.
/// </summary>
[TestClass]
public sealed class WorkloadMathTests
{
    // ---- Median --------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(new[] { 5d }, 5d)]
    [DataRow(new[] { 3d, 1d, 2d }, 2d)]              // odd -> middle of sorted
    [DataRow(new[] { 4d, 1d, 3d, 2d }, 2.5d)]        // even -> mean of two middle
    public void Median_ComputesCorrectly(double[] values, double expected)
    {
        Assert.AreEqual(expected, WorkloadMath.Median(values), 1e-9);
    }

    [TestMethod]
    public void Median_EmptyOrNull_IsZero()
    {
        Assert.AreEqual(0d, WorkloadMath.Median(Array.Empty<double>()));
        Assert.AreEqual(0d, WorkloadMath.Median(null!));
    }

    // ---- MedianAfterDiscardingFirst ------------------------------------------------------------

    [TestMethod]
    public void MedianAfterDiscardingFirst_DropsTheWarmupRun()
    {
        // First (slow) run discarded; median of {2,2,2,10} = 2.
        double median = WorkloadMath.MedianAfterDiscardingFirst(new[] { 99d, 2d, 2d, 2d, 10d });
        Assert.AreEqual(2d, median, 1e-9);
    }

    [TestMethod]
    public void MedianAfterDiscardingFirst_SingleRun_KeepsIt()
    {
        Assert.AreEqual(7d, WorkloadMath.MedianAfterDiscardingFirst(new[] { 7d }), 1e-9);
    }

    [TestMethod]
    public void MedianAfterDiscardingFirst_Empty_IsZero()
    {
        Assert.AreEqual(0d, WorkloadMath.MedianAfterDiscardingFirst(Array.Empty<double>()));
    }

    // ---- BuildMetric ---------------------------------------------------------------------------

    [TestMethod]
    public void BuildMetric_DiscardsFirstAndComputesSpeedup()
    {
        var system = new[] { 100d, 8d, 8d, 8d, 8d }; // discard 100 -> median 8
        var dev = new[] { 50d, 4d, 4d, 4d, 4d };     // discard 50 -> median 4

        WorkloadMetric metric = WorkloadMath.BuildMetric("git clone", "repo", system, dev);

        Assert.IsFalse(metric.Skipped);
        Assert.AreEqual(8d, metric.SystemSeconds, 1e-9);
        Assert.AreEqual(4d, metric.DevSeconds, 1e-9);
        Assert.AreEqual(2d, metric.Speedup, 1e-9);
        // Raw runs preserved for transparency (including the discarded first run).
        Assert.HasCount(5, metric.SystemRuns);
        Assert.HasCount(5, metric.DevRuns);
    }

    [TestMethod]
    public void BuildMetric_CarriesPhaseTimings_AndComputesBuildAndTotal()
    {
        var system = new[] { 5d, 3d, 3d };
        var dev = new[] { 4d, 2d, 2d };

        WorkloadMetric m = WorkloadMath.BuildMetric("dotnet build", "rx", system, dev, prepareSeconds: 45d, setupSeconds: 30d);

        Assert.AreEqual(45d, m.PrepareSeconds, 1e-9);
        Assert.AreEqual(30d, m.SetupSeconds, 1e-9);
        Assert.AreEqual(19d, m.BuildSeconds, 1e-9);  // (5+3+3) + (4+2+2) = 11 + 8
        Assert.AreEqual(94d, m.TotalSeconds, 1e-9);  // 45 + 30 + 19
    }

    [TestMethod]
    public void BuildMetric_NegativePhaseTimings_AreClampedToZero()
    {
        WorkloadMetric m = WorkloadMath.BuildMetric("x", "y", new[] { 2d }, new[] { 1d }, prepareSeconds: -5d, setupSeconds: -3d);

        Assert.AreEqual(0d, m.PrepareSeconds, 1e-9);
        Assert.AreEqual(0d, m.SetupSeconds, 1e-9);
    }

    [TestMethod]
    public void Skipped_ProducesSkippedRowWithReasonAndZeroSpeedup()
    {
        WorkloadMetric metric = WorkloadMath.Skipped("npm ci", "chalk", "npm is not installed");

        Assert.IsTrue(metric.Skipped);
        Assert.AreEqual("npm is not installed", metric.SkipReason);
        Assert.AreEqual(0d, metric.Speedup);
    }

    // ---- Build (headline) ----------------------------------------------------------------------

    [TestMethod]
    public void Build_HeadlineIsMedianOfRunSpeedups_IgnoringSkipped()
    {
        var metrics = new[]
        {
            WorkloadMath.BuildMetric("a", "", new[] { 4d, 4d }, new[] { 2d, 2d }), // speedup 2.0
            WorkloadMath.BuildMetric("b", "", new[] { 9d, 9d }, new[] { 3d, 3d }), // speedup 3.0
            WorkloadMath.BuildMetric("c", "", new[] { 4d, 4d }, new[] { 4d, 4d }), // speedup 1.0
            WorkloadMath.Skipped("d", "", "tool missing"),                          // excluded
        };

        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var preflight = new PreflightInfo { StorageClass = "SSD" };

        WorkloadComparison comparison = WorkloadMath.Build(metrics, now, preflight);

        // Median of {1.0, 2.0, 3.0} = 2.0.
        Assert.AreEqual(2d, comparison.HeadlineSpeedup, 1e-9);
        Assert.AreEqual(now, comparison.CompletedAt);
        Assert.AreEqual(3, comparison.CompletedCount);
        Assert.AreEqual(1, comparison.SkippedCount);
        Assert.AreSame(preflight, comparison.Preflight);
    }

    [TestMethod]
    public void Build_AllSkipped_HeadlineIsZero()
    {
        var metrics = new[] { WorkloadMath.Skipped("a", "", "x"), WorkloadMath.Skipped("b", "", "y") };

        WorkloadComparison comparison = WorkloadMath.Build(metrics, DateTimeOffset.Now, new PreflightInfo());

        Assert.AreEqual(0d, comparison.HeadlineSpeedup);
        Assert.AreEqual(0, comparison.CompletedCount);
        Assert.AreEqual(2, comparison.SkippedCount);
    }
}
