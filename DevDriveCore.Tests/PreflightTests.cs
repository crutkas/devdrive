using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;
using NSubstitute;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for the pre-flight pieces: the pure <see cref="StorageClassFormatter"/>, the static
/// parse/format helpers on <see cref="PreflightProbe"/>, the SDK-&gt;TFM parse on
/// <see cref="DotnetBuildWorkload"/>, and the composition in <see cref="PreflightProbe.Capture"/>
/// (against fakes — no real WMI/process).
/// </summary>
[TestClass]
public sealed class PreflightTests
{
    // ---- StorageClassFormatter -----------------------------------------------------------------

    [TestMethod]
    public void StorageClass_CombinesFriendlyNameMediaAndBus()
    {
        Assert.AreEqual("Samsung SSD 990 · SSD · NVMe", StorageClassFormatter.Format(4, 17, "Samsung SSD 990"));
    }

    [TestMethod]
    public void StorageClass_VirtualDisk_ShowsUnspecifiedAndBus()
    {
        Assert.AreEqual("Msft Virtual Disk · Unspecified · SAS", StorageClassFormatter.Format(0, 10, "Msft Virtual Disk"));
    }

    [TestMethod]
    public void StorageClass_NoFriendlyNameOrBus_FallsBackGracefully()
    {
        Assert.AreEqual("Unspecified", StorageClassFormatter.Format(0, 0, null));
        Assert.AreEqual("HDD · SATA", StorageClassFormatter.Format(3, 11, "   "));
    }

    [TestMethod]
    [DataRow((ushort)4, "SSD")]
    [DataRow((ushort)3, "HDD")]
    [DataRow((ushort)0, "Unspecified")]
    public void MediaTypeLabel_MapsKnownValues(ushort media, string expected)
    {
        Assert.AreEqual(expected, StorageClassFormatter.MediaTypeLabel(media));
    }

    [TestMethod]
    [DataRow((ushort)17, "NVMe")]
    [DataRow((ushort)11, "SATA")]
    [DataRow((ushort)10, "SAS")]
    public void BusTypeLabel_MapsKnownValues(ushort bus, string expected)
    {
        Assert.AreEqual(expected, StorageClassFormatter.BusTypeLabel(bus));
    }

    [TestMethod]
    public void BusTypeLabel_Unknown_IsNull()
    {
        Assert.IsNull(StorageClassFormatter.BusTypeLabel(250));
    }

    // ---- ParsePerformanceMode ------------------------------------------------------------------

    [TestMethod]
    public void ParsePerformanceMode_Zero_IsOff()
    {
        // [int](Get-MpPreference).PerformanceModeStatus == 0 => Disabled => performance mode OFF.
        bool? result = PreflightProbe.ParsePerformanceMode("0");
        Assert.IsTrue(result.HasValue);
        Assert.IsFalse(result.Value);
    }

    [TestMethod]
    public void ParsePerformanceMode_One_IsOn()
    {
        // == 1 => Enabled => performance mode ON (async). Verified empirically on a perf-mode-ON machine.
        bool? result = PreflightProbe.ParsePerformanceMode("1\r\n");
        Assert.IsTrue(result.HasValue);
        Assert.IsTrue(result.Value);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("2")]
    [DataRow("not a number")]
    public void ParsePerformanceMode_UnreadableOrUnexpected_IsNull(string output)
    {
        Assert.IsNull(PreflightProbe.ParsePerformanceMode(output));
    }

    [TestMethod]
    public void ParsePerformanceMode_FallsBackToEnumNames()
    {
        bool? enabled = PreflightProbe.ParsePerformanceMode("Enabled");
        bool? disabled = PreflightProbe.ParsePerformanceMode("Disabled");
        Assert.IsTrue(enabled.HasValue && enabled.Value);
        Assert.IsTrue(disabled.HasValue && !disabled.Value);
    }

    // ---- FormatMachineSummary ------------------------------------------------------------------

    [TestMethod]
    public void FormatMachineSummary_JoinsPresentParts()
    {
        Assert.AreEqual(
            "Intel · 16 logical cores · Windows 10.0.26100",
            PreflightProbe.FormatMachineSummary("Intel", 16, "Windows 10.0.26100"));
    }

    [TestMethod]
    public void FormatMachineSummary_SkipsBlankCpuAndZeroCores()
    {
        Assert.AreEqual("Windows", PreflightProbe.FormatMachineSummary(null, 0, "Windows"));
    }

    // ---- DotnetBuildWorkload.ParseTargetFramework ----------------------------------------------

    [TestMethod]
    [DataRow("10.0.100", "net10.0")]
    [DataRow("8.0.401", "net8.0")]
    [DataRow("9.0.100-preview.2", "net9.0")]
    public void ParseTargetFramework_MapsSdkMajorToTfm(string version, string expected)
    {
        Assert.AreEqual(expected, DotnetBuildWorkload.ParseTargetFramework(version));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("garbage")]
    [DataRow("4.8")] // pre-.NET 5 -> null (can't target net4.0 with the SDK style here)
    public void ParseTargetFramework_Unparseable_IsNull(string version)
    {
        Assert.IsNull(DotnetBuildWorkload.ParseTargetFramework(version));
    }

    // ---- PreflightProbe.Capture (composition over fakes) ---------------------------------------

    [TestMethod]
    public void Capture_ComposesAllProbes()
    {
        var runner = new FakeProcessRunner()
            .Set("powershell", "1")               // performance mode ON (PerformanceModeStatus 1 = Enabled)
            .Set("fsutil", "Trusted : Yes");      // trusted dev drive

        var storage = Substitute.For<IStorageQuery>();
        storage.GetPartitions().Returns(new[] { new StoragePartitionRecord { DriveLetter = 'G', DiskNumber = 1 } });

        var physical = Substitute.For<IPhysicalDiskQuery>();
        physical.GetPhysicalDisks().Returns(new[]
        {
            new PhysicalDiskRecord { DeviceId = 1, MediaType = 4, BusType = 17, FriendlyName = "Test NVMe" },
        });

        var probe = new PreflightProbe(
            runner,
            storage,
            physical,
            freeBytesProbe: root => root == "C:\\" ? 100L : 200L,
            cpuNameProvider: () => "TestCPU",
            osDescriptionProvider: () => "TestOS 10");

        PreflightInfo info = probe.Capture("C:\\", "G:\\", 'G');

        Assert.IsTrue(info.DefenderPerformanceModeOn.HasValue && info.DefenderPerformanceModeOn.Value);
        Assert.IsTrue(info.DevDriveTrusted.HasValue && info.DevDriveTrusted.Value);
        Assert.AreEqual("Test NVMe · SSD · NVMe", info.StorageClass);
        Assert.AreEqual(100L, info.SystemFreeBytes);
        Assert.AreEqual(200L, info.DevFreeBytes);
        StringAssert.Contains(info.MachineSummary, "TestCPU");
        StringAssert.Contains(info.MachineSummary, "TestOS 10");
    }

    [TestMethod]
    public void Capture_DegradesWhenProbesUnavailable()
    {
        var runner = new FakeProcessRunner()
            .Set("powershell", string.Empty, exitCode: -1)              // defender unreadable
            .Set("fsutil", "Access is denied", exitCode: 1);            // trust needs elevation

        var storage = Substitute.For<IStorageQuery>();
        storage.GetPartitions().Returns(Array.Empty<StoragePartitionRecord>());

        var physical = Substitute.For<IPhysicalDiskQuery>();
        physical.GetPhysicalDisks().Returns(Array.Empty<PhysicalDiskRecord>());

        var probe = new PreflightProbe(
            runner,
            storage,
            physical,
            freeBytesProbe: _ => 0L,
            cpuNameProvider: () => null,
            osDescriptionProvider: () => "OS");

        PreflightInfo info = probe.Capture("C:\\", "G:\\", 'G');

        Assert.IsNull(info.DefenderPerformanceModeOn);
        Assert.IsNull(info.DevDriveTrusted);
        Assert.AreEqual("Unknown", info.StorageClass);
    }
}
