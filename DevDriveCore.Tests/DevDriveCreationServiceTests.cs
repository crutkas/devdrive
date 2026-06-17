using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;
using NSubstitute;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for <see cref="DevDriveCreationService"/>. The VHDX path is exercised through a REAL
/// <see cref="VhdProvisioner"/> backed by a mocked <see cref="INativeVhdApi"/> — so no real disk is
/// ever created/attached — and the resize path is asserted to be pure (it must touch no native API).
/// </summary>
[TestClass]
public sealed class DevDriveCreationServiceTests
{
    private const string VhdPath = @"C:\DevDrives\DevDrive.vhdx";

    private static ulong Gib(double g) => (ulong)(g * 1024d * 1024d * 1024d);

    private static (DevDriveCreationService Service, INativeVhdApi Api) NewService()
    {
        var api = Substitute.For<INativeVhdApi>();
        var provisioner = new VhdProvisioner(api, new InMemoryFileSystem(), new InMemoryReversibilityStore());
        return (new DevDriveCreationService(provisioner), api);
    }

    private static DevDriveCreationPlan VhdPlan(bool dynamic = true, ulong? size = null) => new()
    {
        Source = DevDriveCreationSource.Vhdx,
        Label = "DevDrive",
        DriveLetter = 'D',
        SizeBytes = size ?? Gib(64),
        VhdFilePath = VhdPath,
        VhdIsDynamic = dynamic,
    };

    private static DevDriveCreationPlan ResizePlan() => new()
    {
        Source = DevDriveCreationSource.ResizeExistingVolume,
        Label = "DevDrive",
        DriveLetter = 'D',
        SizeBytes = Gib(160),
        SourceVolumeLetter = 'C',
        SourceVolumeSizeBytes = Gib(780),
        SourceVolumeUsedBytes = Gib(360),
        SourceVolumeFreeBytes = Gib(420),
    };

    // ---- VHDX orchestration (mock native API only) ---------------------------------------------

    [TestMethod]
    public async Task CreateVhdDevDriveAsync_BuildsPlanAndProvisionsViaMockApi()
    {
        (DevDriveCreationService service, INativeVhdApi api) = NewService();
        api.AttachVirtualDisk(VhdPath).Returns(@"\\.\PhysicalDrive5");

        DevDriveCreationResult result = await service.CreateVhdDevDriveAsync(VhdPlan());

        Assert.IsTrue(result.Success);
        Assert.AreEqual(DevDriveCreationSource.Vhdx, result.Source);
        Assert.IsTrue(result.FormatPending, "VHDX is created/attached but not yet formatted as a Dev Drive.");
        Assert.AreEqual(5, result.VhdResult!.DiskNumber);
        Assert.AreEqual(@"vhd:" + VhdPath, result.ReversibilityId);
        // C9: the summary must not claim THIS app assigns/formats the drive letter — that is a manual,
        // admin Disk Management step.
        StringAssert.Contains(result.Summary, "Disk Management");
        Assert.IsFalse(result.Summary.Contains("D:", StringComparison.Ordinal), "Must not claim a drive letter is assigned by this app.");
        api.Received(1).CreateVirtualDisk(VhdPath, Gib(64), true);
        api.Received(1).AttachVirtualDisk(VhdPath);
    }

    [TestMethod]
    public async Task CreateVhdDevDriveAsync_FixedDisk_PassesDynamicFalse()
    {
        (DevDriveCreationService service, INativeVhdApi api) = NewService();
        api.AttachVirtualDisk(VhdPath).Returns(@"\\.\PhysicalDrive2");

        await service.CreateVhdDevDriveAsync(VhdPlan(dynamic: false));

        api.Received(1).CreateVirtualDisk(VhdPath, Gib(64), false);
    }

    [TestMethod]
    public async Task CreateVhdDevDriveAsync_WrongSource_Throws()
    {
        (DevDriveCreationService service, _) = NewService();
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await service.CreateVhdDevDriveAsync(ResizePlan()));
    }

    [TestMethod]
    public async Task CreateVhdDevDriveAsync_BelowMinimumSize_Throws()
    {
        (DevDriveCreationService service, _) = NewService();
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => await service.CreateVhdDevDriveAsync(VhdPlan(size: Gib(10))));
    }

    [TestMethod]
    public async Task CreateVhdDevDriveAsync_EmptyPath_Throws()
    {
        (DevDriveCreationService service, _) = NewService();
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await service.CreateVhdDevDriveAsync(VhdPlan() with { VhdFilePath = "  " }));
    }

    [TestMethod]
    public async Task CreateVhdDevDriveAsync_NullPlan_Throws()
    {
        (DevDriveCreationService service, _) = NewService();
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await service.CreateVhdDevDriveAsync(null!));
    }

    // ---- resize is simulation-only (touches no native API) -------------------------------------

    [TestMethod]
    public void SimulateResize_ComputesShrinkAndRemainder()
    {
        (DevDriveCreationService service, _) = NewService();

        DevDriveResizeSimulation sim = service.SimulateResize(ResizePlan());

        Assert.IsTrue(sim.IsSimulationOnly);
        Assert.AreEqual('C', sim.SourceVolumeLetter);
        Assert.AreEqual('D', sim.DevDriveLetter);
        Assert.AreEqual(Gib(160), sim.ShrinkBytes);
        Assert.AreEqual(Gib(160), sim.DevDriveBytes);
        Assert.AreEqual(Gib(620), sim.NewSourceSizeBytes); // 780 - 160
        Assert.AreEqual(Gib(260), sim.NewSourceFreeBytes); // 420 - 160
    }

    [TestMethod]
    public void SimulateResize_Steps_DescribeShrinkPartitionAndDevDriveFormat()
    {
        (DevDriveCreationService service, _) = NewService();

        DevDriveResizeSimulation sim = service.SimulateResize(ResizePlan());

        // F17: the steps must name the REAL public path the gated execute runs (Resize-Partition shrink →
        // New-Partition carve → Format-Volume -DevDrive), not an internal "defrag-engine" the app never uses.
        Assert.IsGreaterThanOrEqualTo(3, sim.Steps.Count);
        Assert.IsTrue(sim.Steps.Any(s => s.Contains("Resize-Partition", StringComparison.Ordinal)));
        Assert.IsTrue(sim.Steps.Any(s => s.Contains("New-Partition", StringComparison.Ordinal)));
        Assert.IsTrue(sim.Steps.Any(s => s.Contains("Format-Volume -DevDrive", StringComparison.Ordinal)));
        Assert.IsFalse(sim.Steps.Any(s => s.Contains("defrag", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void SimulateResize_NeverCallsNativeVhdApi()
    {
        (DevDriveCreationService service, INativeVhdApi api) = NewService();

        service.SimulateResize(ResizePlan());

        api.DidNotReceive().CreateVirtualDisk(Arg.Any<string>(), Arg.Any<ulong>(), Arg.Any<bool>());
        api.DidNotReceive().AttachVirtualDisk(Arg.Any<string>());
        api.DidNotReceive().DetachVirtualDisk(Arg.Any<string>());
    }

    [TestMethod]
    public void SimulateResize_ShrinkExceedsFree_ClampsRemainderToZero()
    {
        (DevDriveCreationService service, _) = NewService();
        DevDriveCreationPlan plan = ResizePlan() with { SizeBytes = Gib(500), SourceVolumeFreeBytes = Gib(420) };

        DevDriveResizeSimulation sim = service.SimulateResize(plan);

        Assert.AreEqual(0UL, sim.NewSourceFreeBytes);
    }

    [TestMethod]
    public void SimulateResize_NullSourceLetter_DefaultsToC()
    {
        (DevDriveCreationService service, _) = NewService();
        DevDriveResizeSimulation sim = service.SimulateResize(ResizePlan() with { SourceVolumeLetter = null });
        Assert.AreEqual('C', sim.SourceVolumeLetter);
    }

    [TestMethod]
    public void SimulateResize_Null_Throws()
    {
        (DevDriveCreationService service, _) = NewService();
        Assert.ThrowsExactly<ArgumentNullException>(() => service.SimulateResize(null!));
    }

    // ---- composition ---------------------------------------------------------------------------

    [TestMethod]
    public void Ctor_NullProvisioner_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new DevDriveCreationService(null!));
    }

    [TestMethod]
    public void CreateDefault_ReturnsInstance()
    {
        Assert.IsNotNull(DevDriveCreationService.CreateDefault());
    }
}
