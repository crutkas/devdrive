using DevDriveStorage;

namespace DevDriveStorage.Tests;

/// <summary>
/// Covers the capacity model behind the volume context strip.
/// </summary>
/// <remarks>
/// The strip is the one control on every storage room, so a wrong bar is wrong everywhere. The two
/// properties that matter are that segments never exceed the volume (a bar that overflows its track
/// reads as corruption) and that unmeasured space is never silently folded into a measured segment —
/// "we have not scanned this" and "this is in use" are different claims and the strip has to keep
/// them apart.
/// </remarks>
[TestClass]
public sealed class VolumeCapacityTests
{
    private static StorageVolume Volume(long capacity, long free) =>
        new("C:\\", "C", "Windows", "NTFS", capacity, free, false, false, false);

    [TestMethod]
    public void UnscannedVolumeReportsOneUsedSegmentAndTheRest()
    {
        VolumeCapacityResult capacity = VolumeCapacity.ForVolume(Volume(1000, 400));

        Assert.HasCount(1, capacity.Segments);
        Assert.AreEqual(CapacitySegmentKind.Used, capacity.Segments[0].Kind);
        Assert.AreEqual(0.6, capacity.Segments[0].Fraction, 0.0001);
        Assert.AreEqual(0.4, capacity.FreeFraction, 0.0001);
    }

    [TestMethod]
    public void ReclaimableIsCarvedOutOfUsedNotAddedToIt()
    {
        // 600 used, of which 150 is reclaimable. The bar must still total 600 — showing 750 would
        // claim the volume is fuller than it is purely because we learned something about it.
        VolumeCapacityResult capacity = VolumeCapacity.ForVolume(Volume(1000, 400), reclaimableBytes: 150);

        Assert.HasCount(2, capacity.Segments);
        Assert.AreEqual(CapacitySegmentKind.Used, capacity.Segments[0].Kind);
        Assert.AreEqual(CapacitySegmentKind.Reclaimable, capacity.Segments[1].Kind);
        Assert.AreEqual(0.45, capacity.Segments[0].Fraction, 0.0001);
        Assert.AreEqual(0.15, capacity.Segments[1].Fraction, 0.0001);

        double total = capacity.Segments.Sum(s => s.Fraction) + capacity.FreeFraction;
        Assert.AreEqual(1.0, total, 0.0001);
    }

    [TestMethod]
    public void ReclaimableLargerThanUsedIsClampedRatherThanOverflowing()
    {
        // Can happen transiently: a scan finishes against a volume whose free space moved underneath
        // it. Clamp instead of drawing a bar wider than its track.
        VolumeCapacityResult capacity = VolumeCapacity.ForVolume(Volume(1000, 900), reclaimableBytes: 500);

        Assert.AreEqual(0.1, capacity.Segments.Sum(s => s.Fraction), 0.0001);
        Assert.IsTrue(capacity.Segments.All(s => s.Fraction >= 0));
    }

    [TestMethod]
    public void FullVolumeLeavesNoFreeFraction()
    {
        VolumeCapacityResult capacity = VolumeCapacity.ForVolume(Volume(1000, 0));

        Assert.AreEqual(0.0, capacity.FreeFraction, 0.0001);
        Assert.AreEqual(1.0, capacity.Segments.Sum(s => s.Fraction), 0.0001);
    }

    [TestMethod]
    public void ZeroCapacityDoesNotDivideByZero()
    {
        VolumeCapacityResult capacity = VolumeCapacity.ForVolume(Volume(0, 0));

        Assert.IsEmpty(capacity.Segments);
        Assert.AreEqual(0.0, capacity.FreeFraction, 0.0001);
    }

    [TestMethod]
    public void CaptionStatesFilesystemAndWhatMakesTheVolumeSpecial()
    {
        StorageVolume devDrive = new("G:\\", "G", "DevDrive", "ReFS", 1000, 400, true, true, true);
        Assert.AreEqual("ReFS · Trusted", VolumeCapacity.CaptionFor(devDrive));

        StorageVolume untrusted = devDrive with { IsTrusted = false };
        Assert.AreEqual("ReFS · Dev Drive", VolumeCapacity.CaptionFor(untrusted));

        // System-ness is passed in rather than sniffed, so the caption is deterministic in a test
        // and does not silently depend on which drive the running machine booted from.
        StorageVolume windows = new("C:\\", "C", "Windows", "NTFS", 1000, 400, false, false, false);
        Assert.AreEqual("NTFS · System", VolumeCapacity.CaptionFor(windows, isSystemVolume: true));
        Assert.AreEqual("NTFS", VolumeCapacity.CaptionFor(windows, isSystemVolume: false));
    }
    // ---- AfterReclaim: the one-bar after-picture ----

    [TestMethod]
    public void AfterReclaimSplitsUsedIntoStillUsedAndFreed()
    {
        // 600 used, 150 of it about to go. The three parts must total the whole volume: a bar whose
        // bands do not add up to 1 leaves a gap that reads as a fourth, unexplained category.
        VolumeCapacityResult bar = VolumeCapacity.AfterReclaim(Volume(1000, 400), freedBytes: 150);

        Assert.HasCount(2, bar.Segments);
        Assert.AreEqual(CapacitySegmentKind.Used, bar.Segments[0].Kind);
        Assert.AreEqual(0.45, bar.Segments[0].Fraction, 0.0001);
        Assert.AreEqual(450, bar.Segments[0].Bytes);
        Assert.AreEqual(CapacitySegmentKind.Freed, bar.Segments[1].Kind);
        Assert.AreEqual(0.15, bar.Segments[1].Fraction, 0.0001);
        Assert.AreEqual(150, bar.Segments[1].Bytes);

        // The track is free-space *before*. Free-after is read off the bar as track plus freed.
        Assert.AreEqual(0.4, bar.FreeFraction, 0.0001);
        Assert.AreEqual(
            1.0,
            bar.Segments.Sum(s => s.Fraction) + bar.FreeFraction,
            0.0001,
            "the three bands must tile the whole volume");
    }

    [TestMethod]
    public void AfterReclaimWithNothingSelectedIsJustTheVolume()
    {
        VolumeCapacityResult bar = VolumeCapacity.AfterReclaim(Volume(1000, 400), freedBytes: 0);

        Assert.HasCount(1, bar.Segments);
        Assert.AreEqual(CapacitySegmentKind.Used, bar.Segments[0].Kind);
        Assert.AreEqual(0.4, bar.FreeFraction, 0.0001);
    }

    [TestMethod]
    public void AfterReclaimClampsFreedToUsedSpace()
    {
        // Free space moves under a finished scan. Freeing more than is in use is arithmetically
        // impossible, and a band wider than its track reads as a rendering bug, not a stale number.
        VolumeCapacityResult bar = VolumeCapacity.AfterReclaim(Volume(1000, 400), freedBytes: 5000);

        Assert.HasCount(1, bar.Segments);
        Assert.AreEqual(CapacitySegmentKind.Freed, bar.Segments[0].Kind);
        Assert.AreEqual(600, bar.Segments[0].Bytes);
        Assert.AreEqual(1.0, bar.Segments[0].Fraction + bar.FreeFraction, 0.0001);
    }

    [TestMethod]
    public void AfterReclaimOnAZeroCapacityVolumeDrawsNothing()
    {
        VolumeCapacityResult bar = VolumeCapacity.AfterReclaim(Volume(0, 0), freedBytes: 100);

        Assert.IsEmpty(bar.Segments);
        Assert.AreEqual(0, bar.FreeFraction, 0.0001);
    }

    // ---- ByRisk: the three-tier composition bar ----

    [TestMethod]
    public void ByRiskFillsItsTrackCompletely()
    {
        // Normalised against the tiers, not a volume. A gap in a risk mix would imply a fourth tier
        // that does not exist.
        VolumeCapacityResult bar = VolumeCapacity.ByRisk(600, 300, 100);

        Assert.HasCount(3, bar.Segments);
        Assert.AreEqual(CapacitySegmentKind.Safe, bar.Segments[0].Kind);
        Assert.AreEqual(0.6, bar.Segments[0].Fraction, 0.0001);
        Assert.AreEqual(CapacitySegmentKind.Check, bar.Segments[1].Kind);
        Assert.AreEqual(0.3, bar.Segments[1].Fraction, 0.0001);
        Assert.AreEqual(CapacitySegmentKind.Careful, bar.Segments[2].Kind);
        Assert.AreEqual(0.1, bar.Segments[2].Fraction, 0.0001);
        Assert.AreEqual(0, bar.FreeFraction, 0.0001);
    }

    [TestMethod]
    public void ByRiskOmitsEmptyTiersRatherThanDrawingZeroWidthBands()
    {
        VolumeCapacityResult bar = VolumeCapacity.ByRisk(600, 0, 400);

        Assert.HasCount(2, bar.Segments);
        Assert.AreEqual(CapacitySegmentKind.Safe, bar.Segments[0].Kind);
        Assert.AreEqual(CapacitySegmentKind.Careful, bar.Segments[1].Kind);
    }

    [TestMethod]
    public void ByRiskWithNothingSelectedIsAnEmptyTrack()
    {
        // Not one grey band: "nothing is selected" and "everything selected is low risk" must not
        // look the same on a bar the user is about to act on.
        VolumeCapacityResult bar = VolumeCapacity.ByRisk(0, 0, 0);

        Assert.IsEmpty(bar.Segments);
        Assert.AreEqual(1, bar.FreeFraction, 0.0001);
    }
}