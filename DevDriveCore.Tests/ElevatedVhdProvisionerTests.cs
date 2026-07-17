using System.Text.Json;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;
using NSubstitute;

namespace DevDriveCore.Tests;

[TestClass]
public sealed class ElevatedVhdProvisionerTests
{
    private const string VhdPath = @"C:\DevDrives\Dev.vhdx";
    private const ulong Size = 64UL * 1024 * 1024 * 1024;

    private static VhdProvisionPlan Plan() => new()
    {
        FilePath = VhdPath,
        MaximumSizeBytes = Size + DevDriveSizeMath.VhdContainerHeadroomBytesExact,
        VolumeSizeBytes = Size,
        DynamicallyExpanding = true,
        DriveLetter = 'V',
        Label = "Dev Drive",
    };

    private static VhdProvisionResult Success() => new()
    {
        Success = true,
        Executed = true,
        Message = "Created V:.",
        FilePath = VhdPath,
        PhysicalPath = @"\\.\PhysicalDrive7",
        DiskNumber = 7,
        DriveLetter = 'V',
        SizeBytes = Size,
        FileSystem = "ReFS",
    };

    [TestMethod]
    public async Task ProvisionAsync_AuthorizesHelperAndKeepsRecoveryReceipt()
    {
        var broker = Substitute.For<IElevatedVhdBroker>();
        VhdBrokerRequest? observed = null;
        broker
            .InvokeAsync(Arg.Any<VhdBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observed = call.Arg<VhdBrokerRequest>();
                return JsonSerializer.Serialize(Success());
            });
        var store = new InMemoryReversibilityStore();
        var provisioner = new ElevatedVhdProvisioner(broker, new InMemoryFileSystem(), store);

        VhdProvisionResult result = await provisioner.ProvisionAsync(Plan());

        Assert.IsTrue(result.Success);
        Assert.AreEqual(VhdProvisioner.ReversibilityId(VhdPath), result.ReversibilityId);
        Assert.IsNotNull(store.TryGet(result.ReversibilityId));
        Assert.IsNotNull(observed);
        Assert.AreEqual(VhdBrokerMode.Create, observed.Mode);
        VhdProvisionPlan sent = JsonSerializer.Deserialize<VhdProvisionPlan>(observed.PlanJson)!;
        Assert.IsTrue(sent.ExecuteAuthorized);
        Assert.AreEqual('V', sent.DriveLetter);
        Assert.AreEqual("Dev Drive", sent.Label);
    }

    [TestMethod]
    public async Task ProvisionAsync_UacDeclined_RemovesIntentReceipt()
    {
        var broker = Substitute.For<IElevatedVhdBroker>();
        broker
            .InvokeAsync(Arg.Any<VhdBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        var store = new InMemoryReversibilityStore();
        var provisioner = new ElevatedVhdProvisioner(broker, new InMemoryFileSystem(), store);

        VhdProvisionResult result = await provisioner.ProvisionAsync(Plan());

        Assert.IsFalse(result.Success);
        Assert.IsFalse(result.Executed);
        Assert.IsEmpty(store.GetAll());
    }

    [TestMethod]
    public async Task ProvisionAsync_ConfirmedRollback_RemovesIntentReceipt()
    {
        var broker = Substitute.For<IElevatedVhdBroker>();
        broker
            .InvokeAsync(Arg.Any<VhdBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Serialize(new VhdProvisionResult
            {
                Success = false,
                Executed = false,
                RolledBack = true,
                Message = "Rolled back.",
                FilePath = VhdPath,
            }));
        var store = new InMemoryReversibilityStore();
        var provisioner = new ElevatedVhdProvisioner(broker, new InMemoryFileSystem(), store);

        VhdProvisionResult result = await provisioner.ProvisionAsync(Plan());

        Assert.IsTrue(result.RolledBack);
        Assert.IsEmpty(store.GetAll());
    }

    [TestMethod]
    public async Task ProvisionAsync_StateUnknown_KeepsIntentReceipt()
    {
        var broker = Substitute.For<IElevatedVhdBroker>();
        broker
            .InvokeAsync(Arg.Any<VhdBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Serialize(new VhdProvisionResult
            {
                Success = false,
                Executed = true,
                StateUnknown = true,
                Message = "Check Disk Management.",
                FilePath = VhdPath,
            }));
        var store = new InMemoryReversibilityStore();
        var provisioner = new ElevatedVhdProvisioner(broker, new InMemoryFileSystem(), store);

        VhdProvisionResult result = await provisioner.ProvisionAsync(Plan());

        Assert.IsTrue(result.StateUnknown);
        Assert.IsNotNull(store.TryGet(VhdProvisioner.ReversibilityId(VhdPath)));
    }

    [TestMethod]
    public async Task ProvisionAsync_MismatchedSuccessBecomesStateUnknown()
    {
        var broker = Substitute.For<IElevatedVhdBroker>();
        broker
            .InvokeAsync(Arg.Any<VhdBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Serialize(Success() with { DriveLetter = 'W' }));
        var store = new InMemoryReversibilityStore();
        var provisioner = new ElevatedVhdProvisioner(broker, new InMemoryFileSystem(), store);

        VhdProvisionResult result = await provisioner.ProvisionAsync(Plan());

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.StateUnknown);
        Assert.IsNotNull(store.TryGet(VhdProvisioner.ReversibilityId(VhdPath)));
    }

    [TestMethod]
    public async Task ProvisionAsync_PreexistingFileNeverInvokesHelper()
    {
        var broker = Substitute.For<IElevatedVhdBroker>();
        var fileSystem = new InMemoryFileSystem().AddFile(VhdPath, "user data");
        var provisioner = new ElevatedVhdProvisioner(
            broker,
            fileSystem,
            new InMemoryReversibilityStore());

        await Assert.ThrowsExactlyAsync<IOException>(() => provisioner.ProvisionAsync(Plan()));

        await broker.DidNotReceive().InvokeAsync(
            Arg.Any<VhdBrokerRequest>(),
            Arg.Any<CancellationToken>());
        Assert.AreEqual("user data", fileSystem.ReadAllText(VhdPath));
    }

    [TestMethod]
    public async Task RevertAsync_AuthorizesHelperAndRemovesReceiptOnSuccess()
    {
        var broker = Substitute.For<IElevatedVhdBroker>();
        VhdBrokerRequest? observed = null;
        broker
            .InvokeAsync(Arg.Any<VhdBrokerRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observed = call.Arg<VhdBrokerRequest>();
                return JsonSerializer.Serialize(new VhdProvisionResult
                {
                    Success = true,
                    Executed = true,
                    Message = "Reverted.",
                    FilePath = VhdPath,
                });
            });
        var fileSystem = new InMemoryFileSystem().AddFile(VhdPath, "vhd");
        var store = new InMemoryReversibilityStore();
        var entry = new ReversibilityEntry
        {
            Id = VhdProvisioner.ReversibilityId(VhdPath),
            Kind = ReversibilityKinds.VhdProvision,
            TargetPath = VhdPath,
        };
        store.Save(entry);
        var provisioner = new ElevatedVhdProvisioner(broker, fileSystem, store);

        await provisioner.RevertAsync(entry);

        Assert.IsNull(store.TryGet(entry.Id));
        Assert.IsNotNull(observed);
        Assert.AreEqual(VhdBrokerMode.Revert, observed.Mode);
        VhdRevertPlan sent = JsonSerializer.Deserialize<VhdRevertPlan>(observed.PlanJson)!;
        Assert.IsTrue(sent.ExecuteAuthorized);
        Assert.AreEqual(VhdPath, sent.FilePath);
    }

    [TestMethod]
    public void Constructor_NullDependenciesThrow()
    {
        var broker = Substitute.For<IElevatedVhdBroker>();
        var fileSystem = new InMemoryFileSystem();
        var store = new InMemoryReversibilityStore();

        Assert.ThrowsExactly<ArgumentNullException>(() => new ElevatedVhdProvisioner(null!, fileSystem, store));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ElevatedVhdProvisioner(broker, null!, store));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ElevatedVhdProvisioner(broker, fileSystem, null!));
    }
}
