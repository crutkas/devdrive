using DevDriveStorage;

namespace DevDriveStorage.Tests;

/// <summary>
/// Covers how the volume context strip is assembled: which volumes appear, in what order, and how
/// reclaim findings are attached to them.
/// </summary>
/// <remarks>
/// The matching rule is the part worth pinning down. Reclaim reports bytes against a path root while
/// volumes are enumerated with their own root string, and the two are produced by different
/// subsystems that have no reason to agree on trailing slashes or letter case. Attaching 400 GB to
/// the wrong bar — or silently to none — is a defect the user would read as the scan being wrong.
/// </remarks>
[TestClass]
public sealed class VolumeStripTests
{
    private static StorageVolume Vol(
        string letter, long capacity = 1000, long free = 400, bool devDrive = false) =>
        new($"{letter}:\\", $"{letter}:", $"Vol{letter}", devDrive ? "ReFS" : "NTFS",
            capacity, free, devDrive, devDrive, false);

    [TestMethod]
    public void EmptyVolumeListGivesEmptyStrip() =>
        Assert.IsEmpty(VolumeStrip.Build([], systemRoot: "C:\\"));

    [TestMethod]
    public void SystemVolumeComesFirstEvenWhenEnumeratedLast()
    {
        var strip = VolumeStrip.Build([Vol("D"), Vol("G", devDrive: true), Vol("C")], "C:\\");

        Assert.AreEqual("C:", strip[0].Volume.DriveLetter);
        Assert.IsTrue(strip[0].IsSystemVolume);
    }

    [TestMethod]
    public void NonSystemVolumesKeepLetterOrder()
    {
        var strip = VolumeStrip.Build([Vol("G", devDrive: true), Vol("D"), Vol("C")], "C:\\");

        CollectionAssert.AreEqual(
            new[] { "C:", "D:", "G:" },
            strip.Select(e => e.Volume.DriveLetter).ToArray());
    }

    [TestMethod]
    public void AccentRoleSeparatesSystemDevDriveAndEverythingElse()
    {
        var strip = VolumeStrip.Build([Vol("C"), Vol("D"), Vol("G", devDrive: true)], "C:\\");

        Assert.AreEqual(VolumeAccentRole.System, strip[0].AccentRole);
        Assert.AreEqual(VolumeAccentRole.Other, strip[1].AccentRole);
        Assert.AreEqual(VolumeAccentRole.DevDrive, strip[2].AccentRole);
    }

    [TestMethod]
    public void ReclaimableMatchesDespiteTrailingSlashAndCaseDifferences()
    {
        // Reclaim reports "c:" while the volume enumerated itself as "C:\". These come from
        // different subsystems, so the strip cannot assume they agree on shape.
        var strip = VolumeStrip.Build(
            [Vol("C"), Vol("G", devDrive: true)],
            "C:\\",
            new Dictionary<string, long> { ["c:"] = 250, ["G:\\"] = 100 });

        Assert.AreEqual(250, strip[0].ReclaimableBytes);
        Assert.AreEqual(100, strip[1].ReclaimableBytes);
    }

    [TestMethod]
    public void VolumeWithNoFindingsReportsZeroRatherThanBeingDropped()
    {
        var strip = VolumeStrip.Build(
            [Vol("C"), Vol("D")], "C:\\", new Dictionary<string, long> { ["C:\\"] = 250 });

        Assert.HasCount(2, strip);
        Assert.AreEqual(0, strip[1].ReclaimableBytes);
    }

    [TestMethod]
    public void FindingsForAVolumeThatIsNotPresentAreIgnored()
    {
        // A removable drive can be unplugged between the scan and the render. Dropping the finding is
        // correct; throwing would take the whole strip down over a drive that no longer matters.
        var strip = VolumeStrip.Build(
            [Vol("C")], "C:\\", new Dictionary<string, long> { ["C:\\"] = 250, ["Z:\\"] = 900 });

        Assert.HasCount(1, strip);
        Assert.AreEqual(250, strip[0].ReclaimableBytes);
    }

    [TestMethod]
    public void NoSystemRootStillProducesAStripWithNoSystemVolume()
    {
        var strip = VolumeStrip.Build([Vol("D"), Vol("C")], systemRoot: null);

        Assert.HasCount(2, strip);
        Assert.IsFalse(strip.Any(e => e.IsSystemVolume));
        Assert.AreEqual("C:", strip[0].Volume.DriveLetter);
    }

    [TestMethod]
    public void AnEmptyStripHasNoDefaultScope() =>
        Assert.IsNull(VolumeStrip.DefaultScope([]));

    /// <summary>
    /// The tightest volume wins, not the first one, not the system one, and not the Dev Drive. Here
    /// C: leads the strip and is the system volume, and G: is the Dev Drive, so anything that picked
    /// on position or role would return C: — the volume with three times the headroom.
    /// </summary>
    [TestMethod]
    public void TheDefaultScopeIsTheVolumeWithTheLeastRoomLeft()
    {
        var strip = VolumeStrip.Build(
            [Vol("C", capacity: 1000, free: 300), Vol("G", capacity: 1000, free: 100, devDrive: true)],
            "C:\\");

        Assert.AreEqual("G:", VolumeStrip.DefaultScope(strip)?.Volume.DriveLetter);
    }

    /// <summary>
    /// Fullness is a fraction, so the volume with the <em>most</em> free bytes can still be the one
    /// that needs looking at. D: has 400 GB free against C:'s 100 and is still the tighter of the
    /// two, at 4% against 10%.
    /// </summary>
    [TestMethod]
    public void FullnessIsAFractionRatherThanFreeBytes()
    {
        var strip = VolumeStrip.Build(
            [Vol("C", capacity: 1_000, free: 100), Vol("D", capacity: 10_000, free: 400)],
            "C:\\");

        Assert.AreEqual("D:", VolumeStrip.DefaultScope(strip)?.Volume.DriveLetter);
    }

    /// <summary>
    /// A volume that reports no capacity cannot be ranked, and must not win the default by dividing
    /// its way to zero percent free. It sorts behind every rankable volume.
    /// </summary>
    [TestMethod]
    public void AVolumeWithUnknownCapacityDoesNotWinTheDefault()
    {
        var strip = VolumeStrip.Build(
            [Vol("C", capacity: 1_000, free: 900), Vol("Z", capacity: 0, free: 0)],
            "C:\\");

        Assert.AreEqual("C:", VolumeStrip.DefaultScope(strip)?.Volume.DriveLetter);
    }

    /// <summary>...but it is still offered when it is the only thing there is.</summary>
    [TestMethod]
    public void AnUnrankableVolumeIsBetterThanNoVolume()
    {
        var strip = VolumeStrip.Build([Vol("Z", capacity: 0, free: 0)], "C:\\");

        Assert.AreEqual("Z:", VolumeStrip.DefaultScope(strip)?.Volume.DriveLetter);
    }

    /// <summary>Two equally full volumes must not open a different room on each visit.</summary>
    [TestMethod]
    public void EquallyFullVolumesBreakTheTieByLetter()
    {
        var strip = VolumeStrip.Build(
            [Vol("G", capacity: 1_000, free: 100, devDrive: true), Vol("D", capacity: 2_000, free: 200)],
            "C:\\");

        Assert.AreEqual("D:", VolumeStrip.DefaultScope(strip)?.Volume.DriveLetter);
    }
}
