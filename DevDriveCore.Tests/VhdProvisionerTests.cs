using System.ComponentModel;
using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;
using NSubstitute;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for <see cref="VhdProvisioner"/> (the app-composed, confirmation-gated VHDX provisioner). The
/// native layer is ALWAYS a mocked <see cref="INativeVhdApi"/> — no test creates, attaches, or detaches
/// a real VHD/VHDX.
/// </summary>
[TestClass]
public sealed class VhdProvisionerTests
{
    private const string VhdPath = @"C:\DevDrives\dev.vhdx";
    private const ulong Size = 64UL * 1024 * 1024 * 1024;

    private static VhdProvisionPlan Plan(string path = VhdPath, ulong size = Size) =>
        new() { FilePath = path, MaximumSizeBytes = size };

    // ---- ParseDiskNumber (pure) ----------------------------------------------------------------

    [TestMethod]
    [DataRow(@"\\.\PhysicalDrive7", 7)]
    [DataRow(@"\\.\PhysicalDrive0", 0)]
    [DataRow("PhysicalDrive123", 123)]
    [DataRow(@"\\.\physicaldrive5", 5)] // case-insensitive
    public void ParseDiskNumber_ParsesNumber(string physicalPath, int expected)
    {
        Assert.AreEqual(expected, VhdProvisioner.ParseDiskNumber(physicalPath));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("no digits here")]
    [DataRow(@"\\.\PhysicalDriveX")]
    public void ParseDiskNumber_ReturnsNull_WhenNoMatch(string? physicalPath)
    {
        Assert.IsNull(VhdProvisioner.ParseDiskNumber(physicalPath));
    }

    // ---- happy path ----------------------------------------------------------------------------

    [TestMethod]
    public async Task ProvisionAsync_CreatesSurfacesAndReportsDiskNumber()
    {
        var api = Substitute.For<INativeVhdApi>();
        api.AttachVirtualDisk(VhdPath).Returns(@"\\.\PhysicalDrive7");
        var fs = new InMemoryFileSystem();
        var store = new InMemoryReversibilityStore();
        var provisioner = new VhdProvisioner(api, fs, store);

        VhdProvisionResult result = await provisioner.ProvisionAsync(Plan());

        Assert.IsTrue(result.Success);
        Assert.AreEqual(VhdPath, result.FilePath);
        Assert.AreEqual(@"\\.\PhysicalDrive7", result.PhysicalPath);
        Assert.AreEqual(7, result.DiskNumber);
        api.Received(1).CreateVirtualDisk(VhdPath, Size, true);
        api.Received(1).AttachVirtualDisk(VhdPath);

        ReversibilityEntry entry = store.TryGet(result.ReversibilityId)!;
        Assert.IsNotNull(entry);
        Assert.AreEqual(ReversibilityKinds.VhdProvision, entry.Kind);
        Assert.AreEqual(VhdPath, entry.TargetPath);
    }

    [TestMethod]
    public async Task ProvisionAsync_UnparseablePhysicalPath_SuccessWithNullDiskNumber()
    {
        var api = Substitute.For<INativeVhdApi>();
        api.AttachVirtualDisk(VhdPath).Returns("something-unexpected");
        var provisioner = new VhdProvisioner(api, new InMemoryFileSystem(), new InMemoryReversibilityStore());

        VhdProvisionResult result = await provisioner.ProvisionAsync(Plan());

        Assert.IsTrue(result.Success);
        Assert.IsNull(result.DiskNumber);
    }

    [TestMethod]
    public async Task ProvisionAsync_FixedSize_PassesDynamicFalse()
    {
        var api = Substitute.For<INativeVhdApi>();
        api.AttachVirtualDisk(VhdPath).Returns(@"\\.\PhysicalDrive1");
        var provisioner = new VhdProvisioner(api, new InMemoryFileSystem(), new InMemoryReversibilityStore());

        await provisioner.ProvisionAsync(new VhdProvisionPlan { FilePath = VhdPath, MaximumSizeBytes = Size, DynamicallyExpanding = false });

        api.Received(1).CreateVirtualDisk(VhdPath, Size, false);
    }

    // ---- failure / rollback --------------------------------------------------------------------

    [TestMethod]
    public async Task ProvisionAsync_CreateFails_DoesNotDeleteUnownedRaceFile()
    {
        var api = Substitute.For<INativeVhdApi>();
        var fs = new InMemoryFileSystem();
        // The target does NOT exist up-front; Create produces a partial file, then fails.
        api.When(x => x.CreateVirtualDisk(Arg.Any<string>(), Arg.Any<ulong>(), Arg.Any<bool>()))
            .Do(_ =>
            {
                fs.AddFile(VhdPath, "partial");
                throw new Win32Exception("create failed");
            });
        var store = new InMemoryReversibilityStore();
        var provisioner = new VhdProvisioner(api, fs, store);

        VhdProvisioningException error = await Assert.ThrowsExactlyAsync<VhdProvisioningException>(
            async () => await provisioner.ProvisionAsync(Plan()));

        Assert.AreEqual(VhdProvisioningStage.Create, error.Stage);
        Assert.IsFalse(error.RollbackConfirmed);
        Assert.IsTrue(fs.FileExists(VhdPath), "A file whose ownership is unproven must never be deleted.");
        api.DidNotReceive().AttachVirtualDisk(Arg.Any<string>());
        Assert.IsEmpty(store.GetAll());
    }

    [TestMethod]
    public async Task ProvisionAsync_TargetFileAlreadyExists_FailsWithoutDeletingIt()
    {
        var api = Substitute.For<INativeVhdApi>();
        var fs = new InMemoryFileSystem().AddFile(VhdPath, "user-data"); // pre-existing file owned by the user
        var store = new InMemoryReversibilityStore();
        var provisioner = new VhdProvisioner(api, fs, store);

        await Assert.ThrowsExactlyAsync<IOException>(async () => await provisioner.ProvisionAsync(Plan()));

        Assert.IsTrue(fs.FileExists(VhdPath), "A pre-existing file must NEVER be deleted on failure.");
        Assert.AreEqual("user-data", fs.ReadAllText(VhdPath), "Pre-existing file contents must be untouched.");
        api.DidNotReceive().CreateVirtualDisk(Arg.Any<string>(), Arg.Any<ulong>(), Arg.Any<bool>());
        api.DidNotReceive().AttachVirtualDisk(Arg.Any<string>());
        Assert.IsEmpty(store.GetAll());
    }

    [TestMethod]
    public async Task ProvisionAsync_CreatesParentDirectory()
    {
        var api = Substitute.For<INativeVhdApi>();
        api.AttachVirtualDisk(VhdPath).Returns(@"\\.\PhysicalDrive2");
        var fs = new InMemoryFileSystem();
        var provisioner = new VhdProvisioner(api, fs, new InMemoryReversibilityStore());

        await provisioner.ProvisionAsync(Plan());

        Assert.IsTrue(fs.DirectoryExists(@"C:\DevDrives"), "Parent directory must be created before CreateVirtualDisk.");
    }

    [TestMethod]
    public async Task ProvisionAsync_AttachFails_DeletesCreatedFile_NoEntry()
    {
        var api = Substitute.For<INativeVhdApi>();
        var fs = new InMemoryFileSystem();
        // Make Create actually produce the backing file (like the real API would).
        api.When(x => x.CreateVirtualDisk(VhdPath, Size, true)).Do(_ => fs.AddFile(VhdPath, "vhd"));
        api.When(x => x.AttachVirtualDisk(VhdPath)).Do(_ => throw new Win32Exception("attach failed"));
        var store = new InMemoryReversibilityStore();
        var provisioner = new VhdProvisioner(api, fs, store);

        VhdProvisioningException error = await Assert.ThrowsExactlyAsync<VhdProvisioningException>(
            async () => await provisioner.ProvisionAsync(Plan()));

        Assert.AreEqual(VhdProvisioningStage.Attach, error.Stage);
        Assert.IsTrue(error.RollbackConfirmed);
        Assert.IsFalse(fs.FileExists(VhdPath), "Created file must be deleted when surfacing fails.");
        Assert.IsEmpty(store.GetAll());
    }

    [TestMethod]
    public async Task ProvisionAsync_ReceiptSaveFails_DeletesFile_DoesNotAttach()
    {
        var api = Substitute.For<INativeVhdApi>();
        var fs = new InMemoryFileSystem();
        api.When(x => x.CreateVirtualDisk(VhdPath, Size, true)).Do(_ => fs.AddFile(VhdPath, "vhd"));
        var store = Substitute.For<IReversibilityStore>();
        store.When(x => x.Save(Arg.Any<ReversibilityEntry>())).Do(_ => throw new IOException("save failed"));
        var provisioner = new VhdProvisioner(api, fs, store);

        await Assert.ThrowsExactlyAsync<IOException>(async () => await provisioner.ProvisionAsync(Plan()));

        // F8: the undo receipt is persisted BEFORE the mount. If it cannot be saved we must NOT attach,
        // and the backing file we created is cleaned up — so an unrevertable attached disk can never exist.
        api.DidNotReceive().AttachVirtualDisk(Arg.Any<string>());
        Assert.IsFalse(fs.FileExists(VhdPath), "Backing file must be deleted when the revert record cannot be saved.");
    }

    [TestMethod]
    public async Task ProvisionAsync_AttachThrowsAfterMount_DetachesBeforeDeleting()
    {
        var api = Substitute.For<INativeVhdApi>();
        var fs = new InMemoryFileSystem();
        api.When(x => x.CreateVirtualDisk(VhdPath, Size, true)).Do(_ => fs.AddFile(VhdPath, "vhd"));
        // F9: AttachVirtualDisk mounts PERMANENT then resolves the physical path, which can throw AFTER
        // the mount has already taken effect — modelled here as a throwing attach over a created file.
        api.When(x => x.AttachVirtualDisk(VhdPath)).Do(_ => throw new Win32Exception("physical path resolution failed"));
        var store = new InMemoryReversibilityStore();
        var provisioner = new VhdProvisioner(api, fs, store);

        VhdProvisioningException error = await Assert.ThrowsExactlyAsync<VhdProvisioningException>(
            async () => await provisioner.ProvisionAsync(Plan()));

        // Must DETACH the possibly-mounted disk BEFORE deleting the (otherwise locked) backing file.
        Received.InOrder(() =>
        {
            api.AttachVirtualDisk(VhdPath);
            api.DetachVirtualDisk(VhdPath);
        });
        Assert.IsTrue(error.RollbackConfirmed);
        Assert.IsFalse(fs.FileExists(VhdPath), "The backing file must be deleted after detaching.");
        Assert.IsEmpty(store.GetAll(), "A fully rolled-back provision leaves no receipt.");
    }

    // ---- revert --------------------------------------------------------------------------------

    [TestMethod]
    public async Task RevertAsync_ByEntry_DetachesAndDeletes()
    {
        var api = Substitute.For<INativeVhdApi>();
        api.AttachVirtualDisk(VhdPath).Returns(@"\\.\PhysicalDrive3");
        var fs = new InMemoryFileSystem();
        api.When(x => x.CreateVirtualDisk(VhdPath, Size, true)).Do(_ => fs.AddFile(VhdPath, "vhd"));
        var store = new InMemoryReversibilityStore();
        var provisioner = new VhdProvisioner(api, fs, store);
        VhdProvisionResult result = await provisioner.ProvisionAsync(Plan());
        Assert.IsTrue(fs.FileExists(VhdPath));

        bool reverted = await provisioner.RevertAsync(result.ReversibilityId);

        Assert.IsTrue(reverted);
        api.Received(1).DetachVirtualDisk(VhdPath);
        Assert.IsFalse(fs.FileExists(VhdPath), "Backing file must be deleted on revert.");
        Assert.IsNull(store.TryGet(result.ReversibilityId));
    }

    [TestMethod]
    public async Task RevertAsync_DetachThrows_KeepsReceiptAndSurfaces()
    {
        var api = Substitute.For<INativeVhdApi>();
        api.When(x => x.DetachVirtualDisk(Arg.Any<string>())).Do(_ => throw new Win32Exception("detach failed"));
        var fs = new InMemoryFileSystem().AddFile(VhdPath, "vhd");
        var store = new InMemoryReversibilityStore();
        store.Save(new ReversibilityEntry { Id = VhdProvisioner.ReversibilityId(VhdPath), Kind = ReversibilityKinds.VhdProvision, TargetPath = VhdPath });
        var provisioner = new VhdProvisioner(api, fs, store);

        // F8(b): a revert whose detach fails must SURFACE the failure and KEEP the receipt so it can be
        // retried — never silently drop the undo record (which would orphan a still-mounted disk).
        await Assert.ThrowsExactlyAsync<Win32Exception>(async () =>
            await provisioner.RevertAsync(VhdProvisioner.ReversibilityId(VhdPath)));

        Assert.IsNotNull(store.TryGet(VhdProvisioner.ReversibilityId(VhdPath)), "Receipt must survive a failed revert.");
        Assert.IsTrue(fs.FileExists(VhdPath), "The backing file must not be deleted when detach fails.");
    }

    [TestMethod]
    public async Task RevertAsync_UnknownId_ReturnsFalse()
    {
        var provisioner = new VhdProvisioner(Substitute.For<INativeVhdApi>(), new InMemoryFileSystem(), new InMemoryReversibilityStore());
        Assert.IsFalse(await provisioner.RevertAsync("vhd:nope"));
    }

    [TestMethod]
    public async Task RevertAsync_WrongKind_ReturnsFalse()
    {
        var store = new InMemoryReversibilityStore();
        store.Save(new ReversibilityEntry { Id = "x", Kind = ReversibilityKinds.PackageCacheMove });
        var provisioner = new VhdProvisioner(Substitute.For<INativeVhdApi>(), new InMemoryFileSystem(), store);

        Assert.IsFalse(await provisioner.RevertAsync("x"));
        Assert.IsNotNull(store.TryGet("x"));
    }

    // ---- guards --------------------------------------------------------------------------------

    [TestMethod]
    public void Ctor_NullArgs_Throw()
    {
        var api = Substitute.For<INativeVhdApi>();
        var fs = new InMemoryFileSystem();
        var store = new InMemoryReversibilityStore();
        Assert.ThrowsExactly<ArgumentNullException>(() => new VhdProvisioner(null!, fs, store));
        Assert.ThrowsExactly<ArgumentNullException>(() => new VhdProvisioner(api, null!, store));
        Assert.ThrowsExactly<ArgumentNullException>(() => new VhdProvisioner(api, fs, null!));
    }

    [TestMethod]
    public async Task ProvisionAsync_InvalidPlan_Throws()
    {
        var provisioner = new VhdProvisioner(Substitute.For<INativeVhdApi>(), new InMemoryFileSystem(), new InMemoryReversibilityStore());
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await provisioner.ProvisionAsync(null!));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await provisioner.ProvisionAsync(Plan(path: "  ")));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () => await provisioner.ProvisionAsync(Plan(size: 0UL)));
    }

    [TestMethod]
    public void ReversibilityId_HasStableFormat()
    {
        Assert.AreEqual(@"vhd:C:\DevDrives\dev.vhdx", VhdProvisioner.ReversibilityId(VhdPath));
    }
}
