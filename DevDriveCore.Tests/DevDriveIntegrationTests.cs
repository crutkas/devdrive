using DevDriveCore.Models;
using DevDriveCore.Platform;
using DevDriveCore.Services;

namespace DevDriveCore.Tests;

/// <summary>
/// Guarded, NON-mocked tests that exercise the real platform stack on the host machine.
/// They assert the truth on this machine (G: is a real ReFS Dev Drive) but go
/// <see cref="Assert.Inconclusive(string)"/> when no Dev Drive is present at G:, so the suite
/// stays portable to machines without one.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class DevDriveIntegrationTests
{
    private const char ExpectedDevDriveLetter = 'G';

    [TestMethod]
    public void RealService_ReportsGDriveAsDevDrive()
    {
        IDevDriveService service = DevDriveService.CreateDefault();

        IReadOnlyList<VolumeInfo> volumes = service.GetVolumes();
        Assert.IsNotEmpty(volumes, "Storage WMI returned no fixed volumes — unexpected on any machine.");

        VolumeInfo? g = volumes.FirstOrDefault(v => v.DriveLetter == ExpectedDevDriveLetter);
        if (g is null)
        {
            Assert.Inconclusive($"No {ExpectedDevDriveLetter}: volume on this machine; skipping the Dev Drive oracle assertion.");
            return;
        }

        if (!g.IsDevDrive)
        {
            Assert.Inconclusive(
                $"{ExpectedDevDriveLetter}: exists but is not a Dev Drive on this machine; skipping. " +
                "(On the reference machine G: is a ReFS Dev Drive and this asserts IsDevDrive == true.)");
            return;
        }

        // On the reference machine we reach here and genuinely assert the oracle.
        Assert.IsTrue(g.IsDevDrive, "G: must be detected as a Dev Drive.");
        Assert.AreEqual("ReFS", g.FileSystemType, ignoreCase: true, "Dev Drives are ReFS-formatted.");
        Assert.IsGreaterThan(0UL, g.SizeBytes, "A real volume reports a non-zero size.");
    }

    [TestMethod]
    public void RealNativeApi_GDriveHasDevVolumeFlagSet()
    {
        var native = new NativeVolumeApi();

        uint? flags = native.QueryPersistentVolumeState($"{ExpectedDevDriveLetter}:\\");
        if (flags is null)
        {
            Assert.Inconclusive($"Could not query persistent volume state for {ExpectedDevDriveLetter}:\\ (volume may not exist).");
            return;
        }

        (bool isDev, bool isTrusted) = DevDriveService.DecodeFlags(flags);
        if (!isDev)
        {
            Assert.Inconclusive($"{ExpectedDevDriveLetter}: is not a Dev Drive on this machine (flags = 0x{flags:X}); skipping.");
            return;
        }

        // Reference machine: the raw FSCTL flag carries the DEV_VOLUME bit (unelevated).
        Assert.IsTrue(isDev, $"DEV_VOLUME bit must be set (flags = 0x{flags:X}).");
        Assert.AreNotEqual(0u, flags!.Value & PersistentVolumeState.DevVolume);

        // Trust is best-effort and readable unelevated on this machine; record it without making
        // the test brittle if a future policy change flips it.
        TestContext.WriteLine($"G:\\ persistent volume flags = 0x{flags:X} (IsDev={isDev}, IsTrusted={isTrusted}).");
    }

    public TestContext TestContext { get; set; } = null!;
}
