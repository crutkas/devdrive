using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Services;
using DevDriveManager.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Tests for the UI-test-only <see cref="SafeFakeVolumeResizer"/>. The fake must produce a realistic
/// feasibility/outcome while NEVER launching the helper, querying Storage, or touching a disk, so the
/// automated UI suite can drive the resize flow safely.
/// </summary>
/// <remarks>
/// These assert that the fake <em>echoes the plan it was given</em>, which is the only property that
/// makes it a usable stand-in: a fake returning hardcoded letters and sizes would let the UI suite pass
/// against plumbing that never reads the form. Asserting its canned constants for their own sake would
/// be testing the fake rather than anything the product decides.
/// <para>
/// <c>MutationComposition.CreateVolumeResizer</c> is covered by <c>CompositionRootTests</c> alongside
/// the other composition-root factories.
/// </para>
/// </remarks>
[TestClass]
public sealed class SafeFakeVolumeResizerTests
{
    private const ulong Gib = 1024UL * 1024UL * 1024UL;

    private static ResizePlan Plan(ulong shrink = 100UL * Gib) =>
        new() { SourceVolumeLetter = 'C', ShrinkBytes = shrink, NewDriveLetter = 'D', Label = "DevDrive" };

    [TestMethod]
    public async Task PreviewEchoesThePlanAndReportsAReadOnlyProbe()
    {
        var resizer = new SafeFakeVolumeResizer();

        foreach (ulong shrink in (ulong[])[100UL * Gib, ResizeGuard.MinimumDevDriveBytes])
        {
            ResizeFeasibility? f = await resizer.PreviewAsync(Plan(shrink));

            Assert.IsNotNull(f, $"the fake must answer a {shrink}-byte plan");
            Assert.IsTrue(f!.CanProceed, f.Reason);
            Assert.AreEqual('C', f.SourceVolumeLetter);
            Assert.AreEqual('D', f.NewDriveLetter);
            Assert.AreEqual(shrink, f.AlignedShrinkBytes, "the preview must reflect the plan, not a constant");
            Assert.IsTrue(f.IsReadOnlyProbe);
        }
    }

    [TestMethod]
    public async Task ExecuteReportsSimulatedSuccessAndSaysNoDiskWasTouched()
    {
        var resizer = new SafeFakeVolumeResizer();

        ResizeExecuteOutcome outcome = await resizer.VerifyAndExecuteAsync(Plan());

        Assert.IsTrue(outcome.Success);
        Assert.IsTrue(outcome.Executed);
        Assert.AreEqual('C', outcome.SourceVolumeLetter);
        Assert.AreEqual('D', outcome.NewDriveLetter);
        Assert.AreEqual(100UL * Gib, outcome.DevDriveBytes, "the outcome must reflect the plan, not a constant");

        // The safety contract, and the reason this class exists at all.
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
}
