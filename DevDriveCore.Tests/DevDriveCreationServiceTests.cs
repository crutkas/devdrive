using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;
using NSubstitute;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for <see cref="DevDriveCreationService"/>. The VHDX provisioner is always mocked.
/// </summary>
[TestClass]
public sealed class DevDriveCreationServiceTests
{
    private const string VhdPath = @"C:\DevDrives\DevDrive.vhdx";

    private static ulong Gib(double g) => (ulong)(g * 1024d * 1024d * 1024d);

    private static (DevDriveCreationService Service, IVhdProvisioner Provisioner) NewService()
    {
        var provisioner = Substitute.For<IVhdProvisioner>();
        provisioner
            .ProvisionAsync(Arg.Any<VhdProvisionPlan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                VhdProvisionPlan plan = call.Arg<VhdProvisionPlan>();
                return new VhdProvisionResult
                {
                    Success = true,
                    Executed = true,
                    Message = $"Created {plan.DriveLetter}: as a ReFS Dev Drive.",
                    FilePath = plan.FilePath,
                    PhysicalPath = @"\\.\PhysicalDrive5",
                    DiskNumber = 5,
                    DriveLetter = plan.DriveLetter,
                    SizeBytes = plan.VolumeSizeBytes,
                    FileSystem = "ReFS",
                    ReversibilityId = VhdProvisioner.ReversibilityId(plan.FilePath),
                };
            });
        return (new DevDriveCreationService(provisioner), provisioner);
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

    // ---- VHDX orchestration --------------------------------------------------------------------

    [TestMethod]
    public async Task CreateVhdDevDriveAsync_BuildsPlanAndProvisionsViaMockApi()
    {
        (DevDriveCreationService service, IVhdProvisioner provisioner) = NewService();

        DevDriveCreationResult result = await service.CreateVhdDevDriveAsync(VhdPlan());

        Assert.IsTrue(result.Success);
        Assert.AreEqual(DevDriveCreationSource.Vhdx, result.Source);
        Assert.IsFalse(result.StateUnknown);
        Assert.AreEqual(5, result.VhdResult!.DiskNumber);
        Assert.AreEqual('D', result.VhdResult.DriveLetter);
        Assert.AreEqual("ReFS", result.VhdResult.FileSystem);
        Assert.AreEqual(@"vhd:" + VhdPath, result.ReversibilityId);
        StringAssert.Contains(result.Summary, "D:");
        await provisioner.Received(1).ProvisionAsync(
            Arg.Is<VhdProvisionPlan>(p =>
                p.FilePath == VhdPath &&
                p.MaximumSizeBytes == Gib(64) + DevDriveSizeMath.VhdContainerHeadroomBytesExact &&
                p.VolumeSizeBytes == Gib(64) &&
                p.DynamicallyExpanding &&
                p.DriveLetter == 'D' &&
                p.Label == "DevDrive"),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task CreateVhdDevDriveAsync_FixedDisk_PassesDynamicFalse()
    {
        (DevDriveCreationService service, IVhdProvisioner provisioner) = NewService();

        await service.CreateVhdDevDriveAsync(VhdPlan(dynamic: false));

        await provisioner.Received(1).ProvisionAsync(
            Arg.Is<VhdProvisionPlan>(p => !p.DynamicallyExpanding),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task CreateVhdDevDriveAsync_StateUnknown_IsPropagated()
    {
        (DevDriveCreationService service, IVhdProvisioner provisioner) = NewService();
        provisioner
            .ProvisionAsync(Arg.Any<VhdProvisionPlan>(), Arg.Any<CancellationToken>())
            .Returns(new VhdProvisionResult
            {
                Success = false,
                Executed = true,
                StateUnknown = true,
                Message = "Check Disk Management.",
                FilePath = VhdPath,
            });

        DevDriveCreationResult result = await service.CreateVhdDevDriveAsync(VhdPlan());

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.StateUnknown);
        Assert.AreEqual("Check Disk Management.", result.Summary);
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
    public void SimulateResize_NeverCallsProvisioner()
    {
        (DevDriveCreationService service, IVhdProvisioner provisioner) = NewService();

        service.SimulateResize(ResizePlan());

        provisioner.DidNotReceive().ProvisionAsync(
            Arg.Any<VhdProvisionPlan>(),
            Arg.Any<CancellationToken>());
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
