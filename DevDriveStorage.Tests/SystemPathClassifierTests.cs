using DevDriveStorage.Live;

namespace DevDriveStorage.Tests;

/// <summary>
/// Covers the expected-system-exclusion split. Before this existed, every whole-volume scan was
/// permanently <see cref="SnapshotCompletion.Partial"/> because <c>System Volume Information</c>
/// denies an unelevated caller on every fixed volume that exists — so the Partial signal fired on
/// healthy scans and carried no information.
/// </summary>
[TestClass]
public sealed class SystemPathClassifierTests
{
    [TestMethod]
    public void SystemVolumeInformationAtAVolumeRootIsAnExpectedExclusion()
    {
        Assert.IsTrue(SystemPathClassifier.IsExpectedSystemExclusion(@"C:\System Volume Information"));
        Assert.IsTrue(SystemPathClassifier.IsExpectedSystemExclusion(@"G:\System Volume Information"));
        Assert.IsTrue(SystemPathClassifier.IsExpectedSystemExclusion(@"g:\system volume information"));
    }

    [TestMethod]
    public void RecycleBinAndRecoveryAtAVolumeRootAreExpectedExclusions()
    {
        Assert.IsTrue(SystemPathClassifier.IsExpectedSystemExclusion(@"C:\$Recycle.Bin"));
        Assert.IsTrue(SystemPathClassifier.IsExpectedSystemExclusion(@"C:\$RECYCLE.BIN"));
        Assert.IsTrue(SystemPathClassifier.IsExpectedSystemExclusion(@"D:\Recovery"));
        Assert.IsTrue(SystemPathClassifier.IsExpectedSystemExclusion(@"D:\Config.Msi"));
    }

    [TestMethod]
    public void TheSameNameDeeperInTheTreeIsARealDenial()
    {
        // A user folder that happens to share the name is not an OS exclusion — if we cannot read it,
        // that is a genuine failure the user should hear about.
        Assert.IsFalse(SystemPathClassifier.IsExpectedSystemExclusion(@"C:\dev\System Volume Information"));
        Assert.IsFalse(SystemPathClassifier.IsExpectedSystemExclusion(@"C:\backup\$Recycle.Bin"));
    }

    [TestMethod]
    public void OrdinaryPathsAreNeverExclusions()
    {
        Assert.IsFalse(SystemPathClassifier.IsExpectedSystemExclusion(@"C:\Users\someone\source"));
        Assert.IsFalse(SystemPathClassifier.IsExpectedSystemExclusion(@"C:\Windows"));
        Assert.IsFalse(SystemPathClassifier.IsExpectedSystemExclusion(string.Empty));
    }

    [TestMethod]
    public void TrailingSeparatorsDoNotChangeTheAnswer()
    {
        Assert.IsTrue(SystemPathClassifier.IsExpectedSystemExclusion(@"C:\System Volume Information\"));
    }

    [TestMethod]
    public async Task AnUnexpectedDenialStillReportsPartial()
    {
        using var fixture = new LiveScanFixture();
        fixture.File(@"readable\a.bin", 4096);
        string denied = fixture.Dir("secret");
        fixture.DenyListing(denied);

        var source = new LiveStorageSnapshotSource();
        StorageSnapshot snapshot = await source.GetSnapshotAsync(
            new StorageSnapshotRequest(fixture.Root), null, CancellationToken.None);

        Assert.AreEqual(SnapshotCompletion.Partial, snapshot.Completion);
        Assert.HasCount(1, snapshot.Coverage.DeniedPaths);
        Assert.IsEmpty(snapshot.Coverage.ExcludedPaths);
    }

    [TestMethod]
    public async Task AReadableTreeIsCompleteWithNothingExcluded()
    {
        using var fixture = new LiveScanFixture();
        fixture.File(@"src\a.bin", 4096);
        fixture.File(@"src\nested\b.bin", 8192);

        var source = new LiveStorageSnapshotSource();
        StorageSnapshot snapshot = await source.GetSnapshotAsync(
            new StorageSnapshotRequest(fixture.Root), null, CancellationToken.None);

        Assert.AreEqual(SnapshotCompletion.Complete, snapshot.Completion);
        Assert.IsEmpty(snapshot.Coverage.DeniedPaths);
        Assert.IsEmpty(snapshot.Coverage.ExcludedPaths);
    }

    [TestMethod]
    public void CoverageKeepsDeniedAndExcludedApart()
    {
        var coverage = new ScanCoverage(
            100, 100,
            [@"C:\dev\locked"],
            null, null,
            [@"C:\System Volume Information"]);

        Assert.HasCount(1, coverage.DeniedPaths);
        Assert.HasCount(1, coverage.ExcludedPaths);
        Assert.AreEqual(@"C:\dev\locked", coverage.DeniedPaths[0]);
        Assert.AreEqual(@"C:\System Volume Information", coverage.ExcludedPaths[0]);
    }

    [TestMethod]
    public void ExcludedPathsDefaultToEmptyForSourcesThatDoNotMeasureThem()
    {
        var coverage = new ScanCoverage(100, 100, []);
        Assert.IsEmpty(coverage.ExcludedPaths);
    }
}
