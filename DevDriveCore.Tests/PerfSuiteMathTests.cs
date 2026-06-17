using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for <see cref="PerfSuiteMath"/> — bar lengths track the <em>raw measured magnitude</em> (longer =
/// larger value), so for lower-is-better seconds the <em>slower</em> drive draws the longest bar, while the
/// faster drive is marked by the "N×" delta. Speedup/IsFavorable/FormatSpeedup still fold both metric
/// directions. All pure math, no I/O.
/// </summary>
[TestClass]
public sealed class PerfSuiteMathTests
{
    private const double Tolerance = 1e-9;

    // ---- Performance: direction folding ------------------------------------------------------

    [TestMethod]
    public void Performance_HigherIsBetter_PassesValueThrough()
    {
        Assert.AreEqual(6180d, PerfSuiteMath.Performance(6180d, higherIsBetter: true), Tolerance);
    }

    [TestMethod]
    public void Performance_LowerIsBetter_InvertsValue()
    {
        Assert.AreEqual(1d / 8.13d, PerfSuiteMath.Performance(8.13d, higherIsBetter: false), Tolerance);
    }

    [DataRow(0d)]
    [DataRow(-5d)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [TestMethod]
    public void Performance_NonPositiveOrNonFinite_IsZero(double value)
    {
        Assert.AreEqual(0d, PerfSuiteMath.Performance(value, higherIsBetter: true), Tolerance);
        Assert.AreEqual(0d, PerfSuiteMath.Performance(value, higherIsBetter: false), Tolerance);
    }

    // ---- BarFractions: bar length tracks the raw magnitude (larger value = longest bar) ------

    [TestMethod]
    public void BarFractions_HigherIsBetter_LargerValueIsFullBar()
    {
        // Disk sequential read from the mock: C 3,920 MB/s vs G 6,180 MB/s. Larger raw value => longest bar
        // (which, for higher-is-better MB/s, is the faster drive).
        (double system, double dev) = PerfSuiteMath.BarFractions(3920d, 6180d, higherIsBetter: true);

        Assert.AreEqual(1d, dev, Tolerance);                 // larger value => longest bar
        Assert.AreEqual(3920d / 6180d, system, Tolerance);   // smaller value => its share
        Assert.IsGreaterThan(system, dev);
    }

    [TestMethod]
    public void BarFractions_LowerIsBetter_SlowerSystemIsFullBar()
    {
        // Build "git clone" from the mock: C 12.8 s vs G 8.13 s — G is faster (fewer seconds), but bars track
        // the raw magnitude, so the SLOWER drive (more seconds) draws the longest bar.
        (double system, double dev) = PerfSuiteMath.BarFractions(12.8d, 8.13d, higherIsBetter: false);

        Assert.AreEqual(1d, system, Tolerance);              // slower (more seconds) => longest bar
        Assert.AreEqual(8.13d / 12.8d, dev, Tolerance);      // faster (fewer seconds) => its share
        Assert.IsGreaterThan(dev, system, "Lower-is-better must draw the slower drive (more seconds) as the longer bar.");
    }

    [TestMethod]
    public void BarFractions_LowerIsBetter_DevHalfTheSecondsIsHalfTheBar()
    {
        // Seconds: C: 8 s vs G: 4 s — G is twice as fast, so its bar is exactly HALF the (longer) system bar,
        // and the green delta reads 2.0× favorable.
        (double system, double dev) = PerfSuiteMath.BarFractions(8d, 4d, higherIsBetter: false);
        double speedup = PerfSuiteMath.Speedup(8d, 4d, higherIsBetter: false);

        Assert.AreEqual(1d, system, Tolerance);              // slower drive (more seconds) => longest bar
        Assert.AreEqual(0.5d, dev, Tolerance);               // faster drive => half-length bar
        Assert.AreEqual(0.5d, dev / system, Tolerance);      // dev bar is exactly half the system bar
        Assert.AreEqual(2d, speedup, Tolerance);             // 2× faster on the Dev Drive
        Assert.IsTrue(PerfSuiteMath.IsFavorable(speedup), "2.0× should read as favorable.");
    }

    [TestMethod]
    public void BarFractions_SmallerDev_ShrinksDevBar()
    {
        // Larger raw system value (higher-is-better) => system gets the full bar, dev its half share.
        (double system, double dev) = PerfSuiteMath.BarFractions(1000d, 500d, higherIsBetter: true);

        Assert.AreEqual(1d, system, Tolerance);
        Assert.AreEqual(0.5d, dev, Tolerance);
    }

    [TestMethod]
    public void BarFractions_UnusableValues_AreZero()
    {
        (double system, double dev) = PerfSuiteMath.BarFractions(0d, 0d, higherIsBetter: true);
        Assert.AreEqual(0d, system, Tolerance);
        Assert.AreEqual(0d, dev, Tolerance);
    }

    // ---- Speedup: unified across direction ---------------------------------------------------

    [TestMethod]
    public void Speedup_HigherIsBetter_IsDevOverSystem()
    {
        Assert.AreEqual(6180d / 3920d, PerfSuiteMath.Speedup(3920d, 6180d, higherIsBetter: true), Tolerance);
    }

    [TestMethod]
    public void Speedup_LowerIsBetter_IsSystemSecondsOverDevSeconds()
    {
        // Fewer seconds on dev => speedup > 1. 12.8 / 8.13 ≈ 1.57.
        Assert.AreEqual(12.8d / 8.13d, PerfSuiteMath.Speedup(12.8d, 8.13d, higherIsBetter: false), Tolerance);
    }

    [TestMethod]
    public void Speedup_DevSlower_IsBelowOne()
    {
        Assert.IsLessThan(1d, PerfSuiteMath.Speedup(1000d, 500d, higherIsBetter: true));
    }

    [TestMethod]
    public void Speedup_UnusableValue_IsZero()
    {
        Assert.AreEqual(0d, PerfSuiteMath.Speedup(0d, 500d, higherIsBetter: true), Tolerance);
        Assert.AreEqual(0d, PerfSuiteMath.Speedup(500d, 0d, higherIsBetter: false), Tolerance);
    }

    // ---- IsFavorable + FormatSpeedup ---------------------------------------------------------

    [DataRow(1.6d, true)]
    [DataRow(1.04d, true)]   // rounds to 1.0× => still favorable
    [DataRow(0.94d, false)]  // rounds to 0.9× => not favorable
    [DataRow(0d, false)]
    [TestMethod]
    public void IsFavorable_JudgesOnRoundedSpeedup(double speedup, bool expected)
    {
        Assert.AreEqual(expected, PerfSuiteMath.IsFavorable(speedup));
    }

    [TestMethod]
    public void FormatSpeedup_RendersOneDecimalTimes()
    {
        Assert.AreEqual("1.6\u00D7", PerfSuiteMath.FormatSpeedup(1.577d));
        Assert.AreEqual("1.4\u00D7", PerfSuiteMath.FormatSpeedup(1.39d));
    }

    [TestMethod]
    public void FormatSpeedup_NotComputable_IsEmDash()
    {
        Assert.AreEqual("\u2014", PerfSuiteMath.FormatSpeedup(0d));
    }
}
