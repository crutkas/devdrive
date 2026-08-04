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
}
