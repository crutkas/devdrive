using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for <see cref="PerfSuiteAggregator"/> — the streaming headline ("1.4× faster on average so far ·
/// k of N complete"). Verifies the geometric-mean averaging, that skips count toward "k of N" but are
/// excluded from the mean, and the caption grammar before/after the first result.
/// </summary>
[TestClass]
public sealed class PerfSuiteAggregatorTests
{
    [TestMethod]
    public void New_BeforeAnyResult_ReportsZeroOfTotalAndEmDash()
    {
        var aggregator = new PerfSuiteAggregator(total: 11);

        Assert.AreEqual(11, aggregator.Total);
        Assert.AreEqual(0, aggregator.Finished);
        Assert.IsFalse(aggregator.HasResult);
        Assert.AreEqual("\u2014", aggregator.HeadlineValue);
        Assert.AreEqual("0 of 11 complete", aggregator.HeadlineCaption);
    }

    [TestMethod]
    public void AddResult_AveragesSpeedupsGeometrically()
    {
        var aggregator = new PerfSuiteAggregator(total: 4);
        aggregator.AddResult(1.6d);
        aggregator.AddResult(1.4d);
        aggregator.AddResult(1.7d);
        aggregator.AddResult(1.3d);

        double expected = SpeedTestMath.GeometricMean(new[] { 1.6d, 1.4d, 1.7d, 1.3d });
        Assert.AreEqual(expected, aggregator.AverageSpeedup, 1e-9);
        Assert.AreEqual(4, aggregator.CompletedWithResult);
        Assert.AreEqual(4, aggregator.Finished);
        Assert.IsTrue(aggregator.HasResult);
        Assert.AreEqual("faster on average so far \u00B7 4 of 4 complete", aggregator.HeadlineCaption);
    }

    [TestMethod]
    public void AddSkip_CountsTowardFinishedButNotTheMean()
    {
        var aggregator = new PerfSuiteAggregator(total: 3);
        aggregator.AddResult(2.0d);
        aggregator.AddSkip();

        Assert.AreEqual(1, aggregator.CompletedWithResult);
        Assert.AreEqual(1, aggregator.Skipped);
        Assert.AreEqual(2, aggregator.Finished);
        // Mean reflects only the single measured row, not the skip.
        Assert.AreEqual(2.0d, aggregator.AverageSpeedup, 1e-9);
        Assert.AreEqual("faster on average so far \u00B7 2 of 3 complete", aggregator.HeadlineCaption);
    }

    [TestMethod]
    public void AddResult_NonComputableSpeedup_CountsAsCompletedButExcludedFromMean()
    {
        var aggregator = new PerfSuiteAggregator(total: 2);
        aggregator.AddResult(0d);     // measured but speedup not computable
        aggregator.AddResult(1.5d);

        Assert.AreEqual(2, aggregator.CompletedWithResult);
        Assert.AreEqual(2, aggregator.Finished);
        Assert.AreEqual(1.5d, aggregator.AverageSpeedup, 1e-9);
    }

    [TestMethod]
    public void HeadlineValue_FormatsAverageAsOneDecimalTimes()
    {
        var aggregator = new PerfSuiteAggregator(total: 2);
        aggregator.AddResult(1.5d);
        aggregator.AddResult(1.5d);

        Assert.AreEqual("1.5\u00D7", aggregator.HeadlineValue);
    }

    [TestMethod]
    public void OnlySkips_HasNoResultAndCountsComplete()
    {
        var aggregator = new PerfSuiteAggregator(total: 3);
        aggregator.AddSkip();
        aggregator.AddSkip();

        Assert.IsFalse(aggregator.HasResult);
        Assert.AreEqual("\u2014", aggregator.HeadlineValue);
        Assert.AreEqual("2 of 3 complete", aggregator.HeadlineCaption);
    }

    [TestMethod]
    public void NegativeTotal_IsClampedToZero()
    {
        var aggregator = new PerfSuiteAggregator(total: -4);
        Assert.AreEqual(0, aggregator.Total);
    }
}
