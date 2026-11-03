using DevDriveStorage.Live;

namespace DevDriveStorage.Tests;

/// <summary>
/// Covers <see cref="DirectoryMeasurer"/> against real temp trees.
/// </summary>
/// <remarks>
/// This class had no tests at all while four of the six reclaim categories sized their rows
/// through it — build outputs, package caches, worktrees and orphans all ask it "how big is
/// this folder", and that number is what the reclaim button then deletes and what the
/// after-picture promises to give back. A measurement that is wrong here is a delete that
/// frees a different amount than it said it would.
/// </remarks>
[TestClass]
public sealed class DirectoryMeasurerTests
{
    private LiveScanFixture _fixture = null!;

    [TestInitialize]
    public void Init() => _fixture = new LiveScanFixture();

    [TestCleanup]
    public void Cleanup() => _fixture.Dispose();

    [TestMethod]
    public void FilesAndFoldersAreCountedSeparatelyAndBytesAreSummed()
    {
        _fixture.File("a.bin", 8192);
        _fixture.File("sub/b.bin", 4096);
        _fixture.File("sub/deeper/c.bin", 4096);

        DirectoryMeasurement measurement = DirectoryMeasurer.Measure(_fixture.Root);

        Assert.AreEqual(3, measurement.FileCount);
        Assert.AreEqual(2, measurement.FolderCount);
        Assert.AreEqual(16384, measurement.ApparentBytes);
    }

    [TestMethod]
    public void AllocatedBytesAreAtLeastApparentBytesForOrdinaryFiles()
    {
        _fixture.File("dense.bin", 100_001);

        DirectoryMeasurement measurement = DirectoryMeasurer.Measure(_fixture.Root);

        Assert.AreEqual(100_001, measurement.ApparentBytes);
        Assert.IsGreaterThanOrEqualTo(
            measurement.ApparentBytes,
            measurement.AllocatedBytes,
            "A dense file cannot cost less on disk than its own length.");
    }

    [TestMethod]
    public void ASparseFileCostsFarLessOnDiskThanItsApparentLength()
    {
        // The reason the measurer reports both numbers: the reclaim bars promise allocated
        // bytes back, and a sparse or CoW file gives back almost nothing despite its length.
        _fixture.SparseFile("sparse.bin", 64L * 1024 * 1024);

        DirectoryMeasurement measurement = DirectoryMeasurer.Measure(_fixture.Root);

        Assert.AreEqual(64L * 1024 * 1024, measurement.ApparentBytes);
        Assert.IsLessThan(
            measurement.ApparentBytes / 2,
            measurement.AllocatedBytes,
            "A sparse file should allocate almost nothing.");
    }

    [TestMethod]
    public void TheNewestWriteComesFromTheDeepestFileNotTheFolderItself()
    {
        // A directory's own timestamp only moves when its immediate children change, so a repo
        // whose deepest source file was edited today can look untouched for a year. Dormancy
        // grading reads this field, and grading decides what gets offered for deletion.
        string old = _fixture.File("stale.bin", 1024);
        string fresh = _fixture.File("src/deep/nested/fresh.bin", 1024);

        var longAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(old, longAgo);
        File.SetLastWriteTimeUtc(fresh, longAgo.AddYears(4));
        Directory.SetLastWriteTimeUtc(_fixture.Root, longAgo);

        DirectoryMeasurement measurement = DirectoryMeasurer.Measure(_fixture.Root);

        Assert.IsNotNull(measurement.NewestWriteUtc);
        Assert.AreEqual(2024, measurement.NewestWriteUtc.Value.Year);
    }

    [TestMethod]
    public void AJunctionAtTheScanRootIsNotFollowed()
    {
        // Measuring through a junction reports bytes that live on the other side of it. For a
        // package cache someone redirected to their Dev Drive by hand — the exact workflow this
        // app recommends — the row would claim to free space on the volume the link sits on
        // while the data is on the volume it points at.
        string target = Path.Combine(
            Path.GetTempPath(), "devdrive-measure-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllBytes(Path.Combine(target, "big.bin"), new byte[256 * 1024]);
            string link = _fixture.Junction("link", target);

            DirectoryMeasurement measurement = DirectoryMeasurer.Measure(link);

            Assert.AreEqual(0, measurement.ApparentBytes, "The target's bytes were counted.");
            Assert.AreEqual(0, measurement.FileCount);
            Assert.IsNull(
                measurement.NewestWriteUtc,
                "A folder we refused to walk must not report a freshness signal it never read.");
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    [TestMethod]
    public void AJunctionBelowTheScanRootIsCountedAsAFolderButNotWalked()
    {
        string target = Path.Combine(
            Path.GetTempPath(), "devdrive-measure-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllBytes(Path.Combine(target, "big.bin"), new byte[256 * 1024]);
            _fixture.File("real.bin", 4096);
            _fixture.Junction("link", target);

            DirectoryMeasurement measurement = DirectoryMeasurer.Measure(_fixture.Root);

            Assert.AreEqual(4096, measurement.ApparentBytes);
            Assert.AreEqual(1, measurement.FileCount);
            Assert.AreEqual(1, measurement.FolderCount, "The junction itself should still count.");
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    [TestMethod]
    public void MaxDepthStopsTheWalkWithoutFailing()
    {
        _fixture.File("top.bin", 1024);
        _fixture.File("one/mid.bin", 1024);
        _fixture.File("one/two/deep.bin", 1024);

        DirectoryMeasurement measurement = DirectoryMeasurer.Measure(_fixture.Root, maxDepth: 1);

        Assert.AreEqual(2048, measurement.ApparentBytes, "The depth-2 file should be excluded.");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void ABlankPathMeasuresEmptyRatherThanThrowing(string path)
    {
        Assert.AreEqual(DirectoryMeasurement.Empty, DirectoryMeasurer.Measure(path));
    }

    [TestMethod]
    public void AMissingPathMeasuresEmptyRatherThanThrowing()
    {
        string missing = Path.Combine(_fixture.Root, "never-created");

        Assert.AreEqual(DirectoryMeasurement.Empty, DirectoryMeasurer.Measure(missing));
    }

    [TestMethod]
    public void AFilePathMeasuresEmptyRatherThanReportingItsOwnSize()
    {
        string file = _fixture.File("a.bin", 4096);

        Assert.AreEqual(DirectoryMeasurement.Empty, DirectoryMeasurer.Measure(file));
    }

    [TestMethod]
    public void ADeniedSubfolderIsSkippedRatherThanFailingTheWholeMeasurement()
    {
        _fixture.File("readable.bin", 8192);
        string denied = _fixture.Dir("locked");
        File.WriteAllBytes(Path.Combine(denied, "hidden.bin"), new byte[4096]);
        _fixture.DenyListing(denied);

        DirectoryMeasurement measurement = DirectoryMeasurer.Measure(_fixture.Root);

        Assert.AreEqual(
            8192,
            measurement.ApparentBytes,
            "A partial measurement is a far better answer than no measurement.");
    }

    [TestMethod]
    public void CancellationStopsTheWalk()
    {
        _fixture.File("a.bin", 1024);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(
            () => DirectoryMeasurer.Measure(_fixture.Root, cts.Token));
    }
}
