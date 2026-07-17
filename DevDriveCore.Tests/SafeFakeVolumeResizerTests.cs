using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;
using DevDriveManager.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for the UI-test-only <see cref="SafeFakeVolumeResizer"/> and the
/// <see cref="MutationComposition.CreateVolumeResizer"/> seam. The fake must produce a realistic
/// feasibility/outcome while NEVER launching the helper, querying Storage, or touching a disk, so the
/// automated UI suite can drive the resize flow safely.
/// </summary>
[TestClass]
public sealed class SafeFakeVolumeResizerTests
{
    private const ulong Gib = 1024UL * 1024UL * 1024UL;

    private static ResizePlan Plan(ulong shrink = 100UL * Gib) =>
        new() { SourceVolumeLetter = 'C', ShrinkBytes = shrink, NewDriveLetter = 'D', Label = "DevDrive" };

    [TestMethod]
    public async Task PreviewAsync_NormalPlan_ReportsCanProceed()
    {
        var resizer = new SafeFakeVolumeResizer();

        ResizeFeasibility? f = await resizer.PreviewAsync(Plan());

        Assert.IsNotNull(f);
        Assert.IsTrue(f!.CanProceed, f.Reason);
        Assert.AreEqual('C', f.SourceVolumeLetter);
        Assert.AreEqual('D', f.NewDriveLetter);
        Assert.AreEqual(100UL * Gib, f.AlignedShrinkBytes);
        Assert.IsTrue(f.IsReadOnlyProbe);
    }

    [TestMethod]
    public async Task PreviewAsync_NeverReturnsNull()
    {
        var resizer = new SafeFakeVolumeResizer();
        Assert.IsNotNull(await resizer.PreviewAsync(Plan(shrink: ResizeGuard.MinimumDevDriveBytes)));
    }

    [TestMethod]
    public async Task VerifyAndExecuteAsync_ReportsSimulatedSuccess_WithoutTouchingDisk()
    {
        var resizer = new SafeFakeVolumeResizer();

        ResizeExecuteOutcome outcome = await resizer.VerifyAndExecuteAsync(Plan());

        Assert.IsTrue(outcome.Success);
        Assert.IsTrue(outcome.Executed);
        Assert.AreEqual('C', outcome.SourceVolumeLetter);
        Assert.AreEqual('D', outcome.NewDriveLetter);
        Assert.AreEqual(100UL * Gib, outcome.DevDriveBytes);
        StringAssert.Contains(outcome.Message, "No real disk", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task NullPlan_Throws()
    {
        var resizer = new SafeFakeVolumeResizer();
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await resizer.PreviewAsync(null!));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await resizer.VerifyAndExecuteAsync(null!));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () => await resizer.ExecuteAsync(null!));
    }

    [TestMethod]
    public void CreateVolumeResizer_ReturnsResizer()
    {
        // In the default (non-seam) environment this is the production VolumeResizer; under the UI-test
        // seam it is a SafeFakeVolumeResizer. Either way it is a non-null IVolumeResizer and constructing
        // it performs no disk I/O.
        IVolumeResizer resizer = MutationComposition.CreateVolumeResizer();
        Assert.IsNotNull(resizer);
    }
}
