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
}
