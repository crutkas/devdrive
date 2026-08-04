using DevDriveCore.Models;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Covers the Overview room's ranking. The order of these rows is the room's entire argument, so it
/// is asserted rather than eyeballed.
/// </summary>
[TestClass]
public sealed class AttentionSignalBuilderTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static VolumeInfo Volume(
        char? letter, long totalGb, long freeGb, bool devDrive = false, string label = "") => new()
        {
            DriveLetter = letter,
            Label = label,
            FileSystemType = devDrive ? "ReFS" : "NTFS",
            SizeBytes = (ulong)(totalGb * Gb),
            FreeBytes = (ulong)(freeGb * Gb),
            IsDevDrive = devDrive,
            IsTrusted = devDrive,
        };

    private static AttentionInputs Healthy(params VolumeInfo[] volumes) => new()
    {
        Volumes = volumes.Length > 0 ? volumes : [Volume('C', 1000, 500), Volume('G', 1000, 500, devDrive: true)],
        HasSpaceScan = true,
    };

    [TestMethod]
    public void Build_OnAHealthyMachineRaisesNothing()
    {
        Assert.IsEmpty(AttentionSignalBuilder.Build(Healthy()));
    }

    [TestMethod]
    public void Build_RaisesLowSpaceBelowTheFloor()
    {
        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(
            Healthy(Volume('C', 1000, 100), Volume('G', 1000, 500, devDrive: true)));

        Assert.HasCount(1, signals);
        Assert.AreEqual("C: is 90% full", signals[0].Title);
        Assert.AreEqual("reclaim", signals[0].RoomTag);
    }

    [TestMethod]
    public void Build_LeavesAVolumeExactlyAtTheFloorAlone()
    {
        Assert.IsEmpty(AttentionSignalBuilder.Build(
            Healthy(Volume('C', 1000, 150), Volume('G', 1000, 500, devDrive: true))));
    }

    [TestMethod]
    public void Build_CallsAVeryFullVolumeBadRatherThanWarn()
    {
        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(
            Healthy(Volume('C', 1000, 40), Volume('G', 1000, 500, devDrive: true)));

        Assert.AreEqual(SignalKind.Bad, signals[0].Kind);
    }

    [TestMethod]
    public void Build_PutsLowSpaceAboveALargerByteGain()
    {
        AttentionInputs inputs = Healthy(Volume('C', 1000, 100), Volume('G', 1000, 500, devDrive: true)) with
        {
            HasReclaimScan = true,
            ReclaimableBytes = 400 * Gb,
            ReclaimSafeBytes = 200 * Gb,
            ReclaimCategoryCount = 7,
        };

        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(inputs);

        Assert.AreEqual("LowSpace_C", signals[0].Id);
        Assert.AreEqual("Reclaimable", signals[1].Id);
    }

    [TestMethod]
    public void Build_RanksByteGainsLargestFirst()
    {
        AttentionInputs inputs = Healthy() with
        {
            HasReclaimScan = true,
            ReclaimableBytes = 20 * Gb,
            ReclaimSafeBytes = 10 * Gb,
            ReclaimCategoryCount = 3,
            CachesOffDevDrive = [new CacheSignalInput("npm", @"C:\npm", 40 * Gb, false)],
            CachesOnDevDrive = 4,
        };

        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(inputs);

        Assert.AreEqual("CacheOffDrive_npm", signals[0].Id);
        Assert.AreEqual("Reclaimable", signals[1].Id);
    }

    [TestMethod]
    public void Build_SaysNothingAboutReclaimBeforeAScan()
    {
        AttentionInputs inputs = Healthy() with { ReclaimableBytes = 400 * Gb };

        Assert.IsEmpty(AttentionSignalBuilder.Build(inputs));
    }

    [TestMethod]
    public void Build_SaysNothingAboutReclaimWhenAScanFoundNothing()
    {
        AttentionInputs inputs = Healthy() with { HasReclaimScan = true, ReclaimableBytes = 0 };

        Assert.IsEmpty(AttentionSignalBuilder.Build(inputs));
    }

    [TestMethod]
    public void Build_NamesTheZeroRiskShareOfAReclaim()
    {
        AttentionInputs inputs = Healthy() with
        {
            HasReclaimScan = true,
            ReclaimableBytes = 200 * Gb,
            ReclaimSafeBytes = 130 * Gb,
            ReclaimCategoryCount = 7,
        };

        Assert.Contains("zero-risk", AttentionSignalBuilder.Build(inputs)[0].Detail);
    }

    [TestMethod]
    public void Build_RaisesEachCacheThatIsNotOnADevDrive()
    {
        AttentionInputs inputs = Healthy() with
        {
            CachesOffDevDrive =
            [
                new CacheSignalInput("pip", @"C:\pip", 3 * Gb, false),
                new CacheSignalInput("cargo", @"C:\cargo", 9 * Gb, true),
            ],
            CachesOnDevDrive = 3,
        };

        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(inputs);

        Assert.HasCount(2, signals);
        Assert.AreEqual("CacheOffDrive_cargo", signals[0].Id);
        Assert.AreEqual("caches", signals[0].RoomTag);
    }

    [TestMethod]
    public void Build_DistinguishesARedirectedCacheFromAnUntouchedOne()
    {
        AttentionInputs inputs = Healthy() with
        {
            CachesOffDevDrive = [new CacheSignalInput("pip", @"D:\pip", 3 * Gb, true)],
            CachesOnDevDrive = 4,
        };

        Assert.Contains("Redirected", AttentionSignalBuilder.Build(inputs)[0].Detail);
    }

    [TestMethod]
    public void Build_RollsUpThreeOrMoreCachesIntoOneRow()
    {
        AttentionInputs inputs = Healthy() with
        {
            CachesOffDevDrive =
            [
                new CacheSignalInput("pip", @"C:\pip", 3 * Gb, false),
                new CacheSignalInput("cargo", @"C:\cargo", 9 * Gb, true),
                new CacheSignalInput("npm", @"D:\npm", 2 * Gb, false),
            ],
            CachesOnDevDrive = 2,
        };

        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(inputs);

        Assert.HasCount(1, signals);
        Assert.AreEqual("CachesOffDrive", signals[0].Id);
        Assert.AreEqual("3 package caches are not on a Dev Drive", signals[0].Title);
    }

    [TestMethod]
    public void Build_SumsTheBytesOfARolledUpCacheRow()
    {
        AttentionInputs inputs = Healthy() with
        {
            CachesOffDevDrive =
            [
                new CacheSignalInput("pip", @"C:\pip", 3 * Gb, false),
                new CacheSignalInput("cargo", @"C:\cargo", 9 * Gb, true),
                new CacheSignalInput("npm", @"D:\npm", 2 * Gb, false),
            ],
        };

        Assert.AreEqual(14 * Gb, AttentionSignalBuilder.Build(inputs)[0].ImpactBytes);
    }

    [TestMethod]
    public void Build_NamesEveryEcosystemInARolledUpCacheRow()
    {
        AttentionInputs inputs = Healthy() with
        {
            CachesOffDevDrive =
            [
                new CacheSignalInput("pip", @"C:\pip", 3 * Gb, false),
                new CacheSignalInput("cargo", @"C:\cargo", 9 * Gb, true),
                new CacheSignalInput("npm", @"C:\npm", 2 * Gb, false),
            ],
        };

        Assert.StartsWith("cargo, npm, pip", AttentionSignalBuilder.Build(inputs)[0].Detail);
    }

    [TestMethod]
    public void Build_ReportsTheDistinctRootsOfARolledUpCacheRow()
    {
        AttentionInputs inputs = Healthy() with
        {
            CachesOffDevDrive =
            [
                new CacheSignalInput("pip", @"C:\pip", 3 * Gb, false),
                new CacheSignalInput("cargo", @"C:\cargo", 9 * Gb, true),
                new CacheSignalInput("npm", @"D:\npm", 2 * Gb, false),
            ],
        };

        Assert.AreEqual("C: and D:", AttentionSignalBuilder.Build(inputs)[0].Where);
    }

    [TestMethod]
    public void Build_SaysNotMeasuredWhenNoRolledUpCacheHasASize()
    {
        AttentionInputs inputs = Healthy() with
        {
            CachesOffDevDrive =
            [
                new CacheSignalInput("pip", @"C:\pip", 0, false),
                new CacheSignalInput("cargo", @"C:\cargo", 0, true),
                new CacheSignalInput("npm", @"C:\npm", 0, false),
            ],
        };

        Assert.AreEqual("not measured", AttentionSignalBuilder.Build(inputs)[0].Impact);
    }

    [TestMethod]
    public void Build_RanksTheMissingScanBelowEveryRealFinding()
    {
        AttentionInputs inputs = Healthy() with
        {
            HasSpaceScan = false,
            CachesOffDevDrive = [new CacheSignalInput("pip", @"C:\pip", 0, false)],
        };

        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(inputs);

        Assert.HasCount(2, signals);
        Assert.AreEqual("NoSpaceScan", signals[^1].Id);
        Assert.IsTrue(signals[^1].IsAdvisory);
    }

    [TestMethod]
    public void Build_SaysNothingAboutCachesWhenThereIsNoDevDriveToMoveThemTo()
    {
        AttentionInputs inputs = Healthy(Volume('C', 1000, 500)) with
        {
            CachesOffDevDrive = [new CacheSignalInput("pip", @"C:\pip", 3 * Gb, false)],
        };

        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(inputs);

        Assert.HasCount(1, signals);
        Assert.AreEqual("NoDevDrive", signals[0].Id);
    }
    [TestMethod]
    public void Build_RoutesTheMissingDevDriveSignalToCreate()
    {
        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(Healthy(Volume('C', 1000, 500)));

        Assert.AreEqual("create", signals[0].RoomTag);
    }

    [TestMethod]
    public void Build_RaisesTheMissingScanUntilOneRuns()
    {
        AttentionInputs inputs = Healthy() with { HasSpaceScan = false };

        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(inputs);

        Assert.HasCount(1, signals);
        Assert.AreEqual("space", signals[0].RoomTag);
    }

    [TestMethod]
    public void Build_NamesEveryLetteredVolumeInTheWhereColumn()
    {
        AttentionInputs inputs = Healthy() with
        {
            HasReclaimScan = true,
            ReclaimableBytes = 10 * Gb,
            ReclaimCategoryCount = 1,
        };

        Assert.AreEqual("C: and G:", AttentionSignalBuilder.Build(inputs)[0].Where);
    }

    [TestMethod]
    public void Build_FallsBackToTheLabelForALetterlessVolume()
    {
        IReadOnlyList<AttentionSignal> signals = AttentionSignalBuilder.Build(
            Healthy(Volume(null, 1000, 10, label: "Recovery"), Volume('G', 1000, 500, devDrive: true)));

        Assert.AreEqual("Recovery is 99% full", signals[0].Title);
    }

    [TestMethod]
    public void Build_SkipsAZeroSizedVolumeRatherThanDividingByIt()
    {
        AttentionInputs inputs = Healthy(
            Volume('C', 0, 0), Volume('G', 1000, 500, devDrive: true));

        Assert.IsEmpty(AttentionSignalBuilder.Build(inputs));
    }
}
