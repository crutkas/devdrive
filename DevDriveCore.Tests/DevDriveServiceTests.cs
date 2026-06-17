using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;
using NSubstitute;

namespace DevDriveCore.Tests;

[TestClass]
public sealed class DevDriveServiceTests
{
    private static (DevDriveService Service, IStorageQuery Storage, INativeVolumeApi Native, IProcessRunner Runner) Build()
    {
        var storage = Substitute.For<IStorageQuery>();
        var native = Substitute.For<INativeVolumeApi>();
        var runner = Substitute.For<IProcessRunner>();

        storage.GetVolumes().Returns(Array.Empty<StorageVolumeRecord>());
        storage.GetPartitions().Returns(Array.Empty<StoragePartitionRecord>());
        storage.GetDisks().Returns(Array.Empty<StorageDiskRecord>());

        return (new DevDriveService(storage, native, runner), storage, native, runner);
    }

    // ---- Flag decoding -------------------------------------------------------------------------

    [TestMethod]
    [DataRow(0x0000u, false, false)] // NTFS / not a dev drive
    [DataRow(0x2000u, true, false)]  // DEV_VOLUME only
    [DataRow(0x6000u, true, true)]   // DEV_VOLUME | TRUSTED_VOLUME
    [DataRow(0x6001u, true, true)]   // + unrelated bits (e.g. short-name-creation-disabled) ignored
    [DataRow(0x4000u, false, true)]  // trusted bit without dev bit (defensive)
    public void DecodeFlags_DecodesDevAndTrustedBits(uint flags, bool expectDev, bool expectTrusted)
    {
        (bool isDev, bool isTrusted) = DevDriveService.DecodeFlags(flags);

        Assert.AreEqual(expectDev, isDev);
        Assert.AreEqual(expectTrusted, isTrusted);
    }

    [TestMethod]
    public void DecodeFlags_NullFlags_ReturnsFalseFalse()
    {
        (bool isDev, bool isTrusted) = DevDriveService.DecodeFlags(null);

        Assert.IsFalse(isDev);
        Assert.IsFalse(isTrusted);
    }

    // ---- GetVolumes: composition + flag decode -------------------------------------------------

    [TestMethod]
    public void GetVolumes_DecodesDevDriveFlagsPerVolume()
    {
        (DevDriveService service, IStorageQuery storage, INativeVolumeApi native, _) = Build();

        storage.GetVolumes().Returns(new[]
        {
            TestData.Volume('C', "NTFS", "Windows", size: 1149853741056, free: 587474919424),
            TestData.Volume('G', "ReFS", "DevDrive", size: 1048508891136, free: 570387447808),
        });
        storage.GetPartitions().Returns(new[] { TestData.Partition('C', 0), TestData.Partition('G', 0) });
        storage.GetDisks().Returns(new[] { TestData.Disk(0, busType: 10, location: "Integrated : Bus 0 : Device 1") });

        native.QueryPersistentVolumeState("C:\\").Returns((uint?)0u); // 0x0000 -> not a dev drive
        native.QueryPersistentVolumeState("G:\\").Returns((uint?)(PersistentVolumeState.DevVolume | PersistentVolumeState.TrustedVolume | 0x1));

        IReadOnlyList<VolumeInfo> volumes = service.GetVolumes();

        Assert.HasCount(2, volumes);

        VolumeInfo c = volumes.Single(v => v.DriveLetter == 'C');
        Assert.IsFalse(c.IsDevDrive);
        Assert.IsFalse(c.IsTrusted);
        Assert.AreEqual("NTFS", c.FileSystemType);
        Assert.IsFalse(c.IsVhd);

        VolumeInfo g = volumes.Single(v => v.DriveLetter == 'G');
        Assert.IsTrue(g.IsDevDrive);
        Assert.IsTrue(g.IsTrusted);
        Assert.AreEqual("ReFS", g.FileSystemType);
        Assert.IsFalse(g.IsVhd, "Bus type 10 (SAS) is not a virtual disk.");
        Assert.AreEqual(1048508891136UL - 570387447808UL, g.UsedBytes);
    }

    [TestMethod]
    public void GetVolumes_OnlyIncludesFixedVolumes()
    {
        (DevDriveService service, IStorageQuery storage, _, _) = Build();

        storage.GetVolumes().Returns(new[]
        {
            TestData.Volume('C', "NTFS", driveType: 3),  // Fixed -> included
            TestData.Volume('D', "UDF", driveType: 5),   // CD-ROM -> excluded
            TestData.Volume('E', "FAT32", driveType: 2), // Removable -> excluded
        });

        IReadOnlyList<VolumeInfo> volumes = service.GetVolumes();

        Assert.HasCount(1, volumes);
        Assert.AreEqual('C', volumes[0].DriveLetter);
    }

    [TestMethod]
    public void GetVolumes_OrdersLetteredFirstThenLetterless()
    {
        (DevDriveService service, IStorageQuery storage, INativeVolumeApi native, _) = Build();

        storage.GetVolumes().Returns(new[]
        {
            TestData.Volume(null, "NTFS", "Recovery", driveType: 3),
            TestData.Volume('G', "ReFS", "DevDrive", driveType: 3),
            TestData.Volume('C', "NTFS", "Windows", driveType: 3),
        });
        native.QueryPersistentVolumeState(Arg.Any<string>()).Returns((uint?)null);

        IReadOnlyList<VolumeInfo> volumes = service.GetVolumes();

        CollectionAssert.AreEqual(
            new char?[] { 'C', 'G', null },
            volumes.Select(v => v.DriveLetter).ToArray());
    }

    [TestMethod]
    public void GetVolumes_DoesNotQueryNativeForLetterlessVolume()
    {
        (DevDriveService service, IStorageQuery storage, INativeVolumeApi native, _) = Build();
        storage.GetVolumes().Returns(new[] { TestData.Volume(null, "NTFS", "Recovery", driveType: 3) });

        IReadOnlyList<VolumeInfo> volumes = service.GetVolumes();

        Assert.HasCount(1, volumes);
        Assert.IsFalse(volumes[0].IsDevDrive);
        native.DidNotReceive().QueryPersistentVolumeState(Arg.Any<string>());
    }

    // ---- VHD detection mapping -----------------------------------------------------------------

    [TestMethod]
    public void GetVolumes_FileBackedVirtualDisk_IsVhdWithPath()
    {
        (DevDriveService service, IStorageQuery storage, INativeVolumeApi native, _) = Build();

        storage.GetVolumes().Returns(new[] { TestData.Volume('X', "ReFS", "Dev", driveType: 3) });
        storage.GetPartitions().Returns(new[] { TestData.Partition('X', 2) });
        storage.GetDisks().Returns(new[] { TestData.Disk(2, busType: 15, location: @"C:\VHDs\dev.vhdx") });
        native.QueryPersistentVolumeState("X:\\").Returns((uint?)PersistentVolumeState.DevVolume);

        VolumeInfo v = service.GetVolumes().Single();

        Assert.IsTrue(v.IsVhd);
        Assert.AreEqual(@"C:\VHDs\dev.vhdx", v.VhdFilePath);
        Assert.IsTrue(v.IsDevDrive);
        Assert.IsFalse(v.IsTrusted);
    }

    [TestMethod]
    public void GetVolumes_VirtualBusType14_IsVhd()
    {
        (DevDriveService service, IStorageQuery storage, INativeVolumeApi native, _) = Build();

        storage.GetVolumes().Returns(new[] { TestData.Volume('X', "ReFS", driveType: 3) });
        storage.GetPartitions().Returns(new[] { TestData.Partition('X', 3) });
        storage.GetDisks().Returns(new[] { TestData.Disk(3, busType: 14) });
        native.QueryPersistentVolumeState(Arg.Any<string>()).Returns((uint?)null);

        VolumeInfo v = service.GetVolumes().Single();

        Assert.IsTrue(v.IsVhd);
        Assert.IsNull(v.VhdFilePath, "Bus type 14 with no path location yields no file path.");
    }

    [TestMethod]
    public void GetVolumes_VhdWithTopologyLocation_HasNoFilePath()
    {
        (DevDriveService service, IStorageQuery storage, INativeVolumeApi native, _) = Build();

        storage.GetVolumes().Returns(new[] { TestData.Volume('X', "ReFS", driveType: 3) });
        storage.GetPartitions().Returns(new[] { TestData.Partition('X', 4) });
        storage.GetDisks().Returns(new[] { TestData.Disk(4, busType: 15, location: "Integrated : Bus 0 : Device 1") });
        native.QueryPersistentVolumeState(Arg.Any<string>()).Returns((uint?)null);

        VolumeInfo v = service.GetVolumes().Single();

        Assert.IsTrue(v.IsVhd);
        Assert.IsNull(v.VhdFilePath);
    }

    [TestMethod]
    public void GetVolumes_PhysicalBusType_IsNotVhd()
    {
        (DevDriveService service, IStorageQuery storage, INativeVolumeApi native, _) = Build();

        storage.GetVolumes().Returns(new[] { TestData.Volume('C', "NTFS", driveType: 3) });
        storage.GetPartitions().Returns(new[] { TestData.Partition('C', 0) });
        storage.GetDisks().Returns(new[] { TestData.Disk(0, busType: 17 /* NVMe */) });
        native.QueryPersistentVolumeState(Arg.Any<string>()).Returns((uint?)null);

        VolumeInfo v = service.GetVolumes().Single();

        Assert.IsFalse(v.IsVhd);
        Assert.IsNull(v.VhdFilePath);
    }

    [TestMethod]
    [DataRow(@"C:\VHDs\dev.vhdx", @"C:\VHDs\dev.vhdx")]
    [DataRow(@"\\server\share\dev.vhdx", @"\\server\share\dev.vhdx")]
    [DataRow("Integrated : Bus 0 : Device 1", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void NormalizeVhdPath_ReturnsPathOnlyWhenPathLike(string? location, string? expected)
    {
        Assert.AreEqual(expected, DevDriveService.NormalizeVhdPath(location));
    }

    // ---- fsutil trust info via the service -----------------------------------------------------

    [TestMethod]
    public void GetDevDriveTrustInfo_PassesUppercasedVolumeToFsutil()
    {
        (DevDriveService service, _, _, IProcessRunner runner) = Build();
        runner.Run("fsutil", Arg.Any<string>()).Returns(new ProcessRunResult(0, TestData.FsutilTrusted, string.Empty));

        service.GetDevDriveTrustInfo('g');

        runner.Received(1).Run("fsutil", "devdrv query G:");
    }

    [TestMethod]
    public void GetDevDriveTrustInfo_ParsesTrustedResult()
    {
        (DevDriveService service, _, _, IProcessRunner runner) = Build();
        runner.Run("fsutil", Arg.Any<string>()).Returns(new ProcessRunResult(0, TestData.FsutilTrusted, string.Empty));

        DevDriveTrustInfo? info = service.GetDevDriveTrustInfo('G');

        Assert.IsNotNull(info);
        Assert.AreEqual(DevDriveTrustState.Trusted, info.TrustState);
        Assert.IsTrue(info.PerformanceModeOn);
    }

    [TestMethod]
    public void GetDevDriveTrustInfo_AccessDenied_ReturnsNull()
    {
        (DevDriveService service, _, _, IProcessRunner runner) = Build();
        runner.Run("fsutil", Arg.Any<string>())
              .Returns(new ProcessRunResult(1, TestData.FsutilAccessDenied, string.Empty));

        DevDriveTrustInfo? info = service.GetDevDriveTrustInfo('G');

        Assert.IsNull(info, "Unelevated access-denied must degrade to null, not throw.");
    }

    // ---- GetDefenderPerformanceMode (global Get-MpPreference probe; works unelevated) -----------

    [TestMethod]
    [DataRow("0", false)]  // PerformanceModeStatus 0 = Disabled -> off (synchronous)
    [DataRow("1", true)]   // PerformanceModeStatus 1 = Enabled  -> on (asynchronous)
    public void GetDefenderPerformanceMode_ParsesNumericStatus(string stdout, bool expected)
    {
        (DevDriveService service, _, _, IProcessRunner runner) = Build();
        runner.Run("powershell", Arg.Any<string>()).Returns(new ProcessRunResult(0, stdout, string.Empty));

        bool? result = service.GetDefenderPerformanceMode();

        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void GetDefenderPerformanceMode_NegativeExit_ReturnsNull()
    {
        (DevDriveService service, _, _, IProcessRunner runner) = Build();
        runner.Run("powershell", Arg.Any<string>()).Returns(new ProcessRunResult(-1, string.Empty, string.Empty));

        Assert.IsNull(service.GetDefenderPerformanceMode(), "A non-launch (negative exit) degrades to unknown.");
    }

    [TestMethod]
    public void GetDefenderPerformanceMode_TimedOut_ReturnsNull()
    {
        (DevDriveService service, _, _, IProcessRunner runner) = Build();
        runner.Run("powershell", Arg.Any<string>())
              .Returns(new ProcessRunResult(0, "0", string.Empty) { TimedOut = true });

        Assert.IsNull(service.GetDefenderPerformanceMode(), "A timed-out probe degrades to unknown.");
    }

    [TestMethod]
    public void GetDefenderPerformanceMode_Garbage_ReturnsNull()
    {
        (DevDriveService service, _, _, IProcessRunner runner) = Build();
        runner.Run("powershell", Arg.Any<string>()).Returns(new ProcessRunResult(0, "not-a-number", string.Empty));

        Assert.IsNull(service.GetDefenderPerformanceMode());
    }
}
