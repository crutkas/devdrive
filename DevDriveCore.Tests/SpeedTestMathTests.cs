using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

[TestClass]
public sealed class SpeedTestMathTests
{
    private static DiskBenchmarkResult Result(double seqW, double seqR, double randR, double randW) => new()
    {
        SequentialWriteMBps = seqW,
        SequentialReadMBps = seqR,
        RandomRead4KIops = randR,
        RandomWrite4KIops = randW,
    };

    [TestMethod]
    public void Build_ComputesPerMetricRatios()
    {
        DiskBenchmarkResult system = Result(100, 200, 1000, 500);
        DiskBenchmarkResult dev = Result(200, 400, 3000, 1500);

        SpeedTestComparison cmp = SpeedTestMath.Build(system, dev, DateTimeOffset.UnixEpoch);

        Assert.HasCount(4, cmp.Metrics);
        Assert.AreEqual(2.0, cmp.Metrics.Single(m => m.Kind == SpeedMetricKind.SequentialWrite).Ratio, 1e-9);
        Assert.AreEqual(2.0, cmp.Metrics.Single(m => m.Kind == SpeedMetricKind.SequentialRead).Ratio, 1e-9);
        Assert.AreEqual(3.0, cmp.Metrics.Single(m => m.Kind == SpeedMetricKind.RandomRead4K).Ratio, 1e-9);
        Assert.AreEqual(3.0, cmp.Metrics.Single(m => m.Kind == SpeedMetricKind.RandomWrite4K).Ratio, 1e-9);
    }

    [TestMethod]
    public void Build_HeadlineIsGeometricMeanOfRatios()
    {
        DiskBenchmarkResult system = Result(100, 200, 1000, 500);
        DiskBenchmarkResult dev = Result(200, 400, 3000, 1500); // ratios 2,2,3,3

        SpeedTestComparison cmp = SpeedTestMath.Build(system, dev, DateTimeOffset.UnixEpoch);

        double expected = Math.Pow(2.0 * 2.0 * 3.0 * 3.0, 1.0 / 4.0); // ~2.449
        Assert.AreEqual(expected, cmp.HeadlineMultiplier, 1e-9);
    }

    [TestMethod]
    public void Build_CarriesMetadataAndRawResults()
    {
        DiskBenchmarkResult system = Result(1, 1, 1, 1);
        DiskBenchmarkResult dev = Result(2, 2, 2, 2);
        DateTimeOffset when = new(2026, 6, 11, 10, 0, 0, TimeSpan.Zero);

        SpeedTestComparison cmp = SpeedTestMath.Build(system, dev, when);

        Assert.AreEqual(when, cmp.CompletedAt);
        Assert.AreSame(system, cmp.SystemResult);
        Assert.AreSame(dev, cmp.DevResult);
        Assert.AreEqual("MB/s", cmp.Metrics.Single(m => m.Kind == SpeedMetricKind.SequentialWrite).Unit);
        Assert.AreEqual("IOPS", cmp.Metrics.Single(m => m.Kind == SpeedMetricKind.RandomRead4K).Unit);
    }

    [TestMethod]
    public void Build_ZeroSystemValue_RatioIsZeroAndIgnoredByHeadline()
    {
        DiskBenchmarkResult system = Result(0, 200, 1000, 500); // seq write system == 0
        DiskBenchmarkResult dev = Result(200, 400, 2000, 1000); // ratios: (ignored),2,2,2

        SpeedTestComparison cmp = SpeedTestMath.Build(system, dev, DateTimeOffset.UnixEpoch);

        Assert.AreEqual(0.0, cmp.Metrics.Single(m => m.Kind == SpeedMetricKind.SequentialWrite).Ratio, 1e-9);
        Assert.AreEqual(2.0, cmp.HeadlineMultiplier, 1e-9, "Headline should be the geomean of the three usable ratios (all 2).");
    }

    [TestMethod]
    [DataRow(new[] { 4.0 }, 4.0)]
    [DataRow(new[] { 2.0, 8.0 }, 4.0)]
    [DataRow(new[] { 1.0, 1.0, 1.0, 1.0 }, 1.0)]
    [DataRow(new[] { 2.0, 2.0, 3.0, 3.0 }, 2.449489742783178)]
    public void GeometricMean_ComputesExpected(double[] values, double expected)
    {
        Assert.AreEqual(expected, SpeedTestMath.GeometricMean(values), 1e-9);
    }

    [TestMethod]
    public void GeometricMean_EmptyOrAllNonPositive_ReturnsZero()
    {
        Assert.AreEqual(0.0, SpeedTestMath.GeometricMean(Array.Empty<double>()));
        Assert.AreEqual(0.0, SpeedTestMath.GeometricMean(new[] { 0.0, -1.0 }));
    }

    [TestMethod]
    public void GeometricMean_SkipsNonPositiveEntries()
    {
        // Only 2 and 8 are usable -> geomean 4.
        Assert.AreEqual(4.0, SpeedTestMath.GeometricMean(new[] { 2.0, 0.0, 8.0 }), 1e-9);
    }
}
