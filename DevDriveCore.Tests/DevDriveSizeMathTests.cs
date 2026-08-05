using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for the pure Treatment&#160;A size math: the clamp/convert/fraction logic that keeps the
/// Slider, NumberBox and draggable disk-bar handle in sync. No XAML is instantiated.
/// </summary>
[TestClass]
public sealed class DevDriveSizeMathTests
{
    // Binary GiB as a double (powers of two are exact doubles, so byte equalities are exact).
    private static double Gib(double g) => g * DevDriveSizeMath.BytesPerGigabyte;

    // The mock's reference scenario: 780 GiB source, 360 GiB used, 420 GiB free/selectable.
    private const double TotalGib = 780d;
    private const double UsedGib = 360d;
    private const double MaxGib = 420d;

    // ---- clamp ---------------------------------------------------------------------------------

    [TestMethod]
    public void ClampSizeBytes_InRange_ReturnsRequested()
    {
        Assert.AreEqual(Gib(160), DevDriveSizeMath.ClampSizeBytes(Gib(160), Gib(MaxGib)));
    }

    [TestMethod]
    public void ClampSizeBytes_BelowMinimum_ReturnsFiftyGiB()
    {
        Assert.AreEqual(DevDriveSizeMath.MinimumSizeBytes, DevDriveSizeMath.ClampSizeBytes(Gib(40), Gib(MaxGib)));
        Assert.AreEqual(DevDriveSizeMath.MinimumSizeBytes, Gib(50)); // sanity: minimum is 50 GiB
    }

    [TestMethod]
    public void ClampSizeBytes_AboveMaximum_ReturnsMaximum()
    {
        Assert.AreEqual(Gib(MaxGib), DevDriveSizeMath.ClampSizeBytes(Gib(500), Gib(MaxGib)));
    }

    [TestMethod]
    public void ClampSizeBytes_MaximumBelowMinimum_ReturnsMinimum()
    {
        // Degenerate (source too small): minimum wins, matching the reference wizard's clamp order.
        Assert.AreEqual(DevDriveSizeMath.MinimumSizeBytes, DevDriveSizeMath.ClampSizeBytes(Gib(30), Gib(20)));
    }

    [TestMethod]
    public void IsBelowMinimum_And_ExceedsMaximum_FlagTypedInput()
    {
        Assert.IsTrue(DevDriveSizeMath.IsBelowMinimum(Gib(49)));
        Assert.IsFalse(DevDriveSizeMath.IsBelowMinimum(Gib(50)));
        Assert.IsTrue(DevDriveSizeMath.ExceedsMaximum(Gib(421), Gib(MaxGib)));
        Assert.IsFalse(DevDriveSizeMath.ExceedsMaximum(Gib(420), Gib(MaxGib)));
    }

    // ---- unit conversion (slider <-> number) ---------------------------------------------------

    [TestMethod]
    public void BytesToUnit_Gigabytes_And_Megabytes()
    {
        Assert.AreEqual(160d, DevDriveSizeMath.BytesToUnit(Gib(160), DevDriveSizeUnit.Gigabytes), 1e-9);
        Assert.AreEqual(160d * 1024d, DevDriveSizeMath.BytesToUnit(Gib(160), DevDriveSizeUnit.Megabytes), 1e-9);
    }

    [TestMethod]
    public void UnitToBytes_RoundTripsBothUnits()
    {
        Assert.AreEqual(Gib(160), DevDriveSizeMath.UnitToBytes(160d, DevDriveSizeUnit.Gigabytes), 1e-3);
        Assert.AreEqual(Gib(160), DevDriveSizeMath.UnitToBytes(160d * 1024d, DevDriveSizeUnit.Megabytes), 1e-3);
    }

    [TestMethod]
    public void GigabytesBytes_RoundTrip()
    {
        Assert.AreEqual(160d, DevDriveSizeMath.BytesToGigabytes(Gib(160)), 1e-9);
        Assert.AreEqual(Gib(160), DevDriveSizeMath.GigabytesToBytes(160d), 1e-3);
    }

    // ---- remaining after resize ----------------------------------------------------------------

    [TestMethod]
    public void RemainingBytes_IsMaxMinusSelected_AndNeverNegative()
    {
        Assert.AreEqual(Gib(260), DevDriveSizeMath.RemainingBytes(Gib(MaxGib), Gib(160)));
        Assert.AreEqual(0d, DevDriveSizeMath.RemainingBytes(Gib(MaxGib), Gib(500)));
    }

    // ---- disk-bar fractions --------------------------------------------------------------------

    [TestMethod]
    public void Fractions_MatchExpectedGeometry()
    {
        Assert.AreEqual(UsedGib / TotalGib, DevDriveSizeMath.UsedFraction(Gib(TotalGib), Gib(UsedGib)), 1e-9);
        Assert.AreEqual(260d / TotalGib, DevDriveSizeMath.RemainingFraction(Gib(TotalGib), Gib(MaxGib), Gib(160)), 1e-9);
    }

    [TestMethod]
    public void Fractions_TotalZero_ReturnsZero()
    {
        Assert.AreEqual(0d, DevDriveSizeMath.UsedFraction(0d, Gib(10)));
        Assert.AreEqual(0d, DevDriveSizeMath.RemainingFraction(0d, Gib(10), Gib(5)));
    }

    // ---- Default size ------------------------------------------------------------------------
    // The rule these prove: a create form must never open on "take everything". Each case below is
    // one band of source size, because the default is a different compromise in each.

    [TestMethod]
    public void DefaultSize_RoomySource_TakesThePreferredSize()
    {
        // 420 GiB selectable is more than 256 + 45, so nothing forces a compromise.
        Assert.AreEqual(Gib(256), DevDriveSizeMath.DefaultSizeBytes(Gib(MaxGib)), 1d);
    }

    [TestMethod]
    public void DefaultSize_ExactlyPreferredPlusReserve_StillTakesThePreferredSize()
    {
        Assert.AreEqual(Gib(256), DevDriveSizeMath.DefaultSizeBytes(Gib(301)), 1d);
    }

    [TestMethod]
    public void DefaultSize_ModestSource_LeavesTheReserveBehind()
    {
        // The real shape of this machine: ~101 GiB shrinkable. Before this rule the default was the
        // whole 101, i.e. a proposal to leave the system volume with nothing.
        Assert.AreEqual(Gib(56), DevDriveSizeMath.DefaultSizeBytes(Gib(101)), 1d);
    }

    [TestMethod]
    public void DefaultSize_NeverProposesTakingEverything()
    {
        foreach (double maxGib in new[] { 96d, 101d, 150d, 200d, 300d, 420d, 4000d })
        {
            double selectable = Gib(maxGib);
            Assert.IsLessThan(
                selectable,
                DevDriveSizeMath.DefaultSizeBytes(selectable),
                $"Default consumed the entire {maxGib} GiB source.");
        }
    }

    [TestMethod]
    public void DefaultSize_TightSource_GivesUpTheReserveForAViableDrive()
    {
        // 60 GiB minus the 45 GiB reserve is 15 GiB, which is not a Dev Drive. The 50 GiB floor wins
        // and the reserve is what gets sacrificed, because a sub-minimum drive cannot exist at all.
        Assert.AreEqual(DevDriveSizeMath.MinimumSizeBytes, DevDriveSizeMath.DefaultSizeBytes(Gib(60)));
    }

    [TestMethod]
    public void DefaultSize_SourceTooSmall_StillReturnsTheMinimum()
    {
        // Callers gate on HasEligibleSource; the clamp must not return something below the platform
        // minimum just because the source cannot honour it.
        Assert.AreEqual(DevDriveSizeMath.MinimumSizeBytes, DevDriveSizeMath.DefaultSizeBytes(Gib(10)));
        Assert.AreEqual(DevDriveSizeMath.MinimumSizeBytes, DevDriveSizeMath.DefaultSizeBytes(0d));
        Assert.AreEqual(DevDriveSizeMath.MinimumSizeBytes, DevDriveSizeMath.DefaultSizeBytes(-1d));
    }

    [TestMethod]
    public void DefaultSize_IsAlwaysInRange()
    {
        foreach (double maxGib in new[] { 0d, 10d, 50d, 60d, 96d, 101d, 300d, 4000d })
        {
            double selectable = Gib(maxGib);
            double actual = DevDriveSizeMath.DefaultSizeBytes(selectable);
            Assert.AreEqual(DevDriveSizeMath.ClampSizeBytes(actual, selectable), actual, 1d);
        }
    }
}
