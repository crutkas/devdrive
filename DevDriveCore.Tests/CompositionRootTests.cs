namespace DevDriveCore.Tests;

using DevDriveCore.Abstractions;
using DevDriveCore.Services;
using DevDriveManager.Services;

/// <summary>
/// One smoke test over every parameterless composition-root factory, replacing six near-identical
/// <c>Assert.IsNotNull(X.CreateDefault())</c> tests that were spread across six files.
/// </summary>
/// <remarks>
/// <para>
/// Those six could not fail. Each factory ends in <c>new</c>, so the reference is never null and the
/// assertion expressed nothing — only <em>construction throwing</em> could have failed them, and that
/// is not what they said. They were also silently duplicated work: six copies of one claim.
/// </para>
/// <para>
/// The claim worth keeping is that the composition root wires a real dependency graph together on this
/// OS without throwing, so that is what this asserts — plus the contract each factory promises, which
/// is strictly more than the old tests checked. Wiring is why these are worth testing at all:
/// <see cref="MutationComposition.CreateVolumeResizer"/> in particular returns the production resizer
/// or the safe fake depending on the UI-test seam, and both branches must construct.
/// </para>
/// <para>
/// Nothing here performs disk I/O: every factory only constructs objects. The elevated work happens on
/// the methods, which the per-service test classes cover against in-memory brokers.
/// </para>
/// </remarks>
[TestClass]
public sealed class CompositionRootTests
{
    [TestMethod]
    public void EveryDefaultFactoryConstructsItsContract()
    {
        (string Name, Func<object> Build, Type Contract)[] factories =
        [
            ("VolumeResizer.CreateDefault", VolumeResizer.CreateDefault, typeof(IVolumeResizer)),
            ("VhdProvisioner.CreateDefault", VhdProvisioner.CreateDefault, typeof(IVhdProvisioner)),
            ("PackageCacheMover.CreateDefault", PackageCacheMover.CreateDefault, typeof(IPackageCacheMover)),
            ("InstalledToolDetector.CreateDefault", InstalledToolDetector.CreateDefault, typeof(IInstalledToolDetector)),
            ("DevDriveCreationService.CreateDefault", DevDriveCreationService.CreateDefault, typeof(IDevDriveCreationService)),
            ("MutationComposition.CreateVolumeResizer", MutationComposition.CreateVolumeResizer, typeof(IVolumeResizer)),
        ];

        foreach ((string name, Func<object> build, Type contract) in factories)
        {
            object built;
            try
            {
                built = build();
            }
            catch (Exception exception)
            {
                Assert.Fail($"{name} threw while composing its dependency graph: {exception}");
                return;
            }

            Assert.IsInstanceOfType(
                built,
                contract,
                $"{name} must return a {contract.Name}, not a {built.GetType().Name}.");
        }
    }
}
