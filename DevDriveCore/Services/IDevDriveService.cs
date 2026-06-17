using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// UI-agnostic Dev Drive query surface. Milestone 1 is strictly READ-ONLY: it enumerates volumes
/// and reads Dev Drive state; it never formats, partitions, moves files, or writes env vars.
/// </summary>
public interface IDevDriveService
{
    /// <summary>
    /// Enumerates fixed volumes and computes Dev Drive / Trusted / VHD state for each. Safe to call
    /// unelevated. Intended to run on a background thread (it performs WMI + native I/O).
    /// </summary>
    IReadOnlyList<VolumeInfo> GetVolumes();

    /// <summary>
    /// Reads trust + filter detail for a Dev Drive via <c>fsutil devdrv query</c>. Returns
    /// <c>null</c> when the detail can't be read (almost always because the process is not
    /// elevated) — callers should surface "Run as admin to see filters" rather than an error.
    /// </summary>
    DevDriveTrustInfo? GetDevDriveTrustInfo(char driveLetter);

    /// <summary>
    /// Reads the global Microsoft Defender performance-mode preference
    /// (<c>Get-MpPreference PerformanceModeStatus</c>): <c>true</c> = on (1/Enabled, asynchronous
    /// scanning), <c>false</c> = off (0/Disabled, synchronous), <c>null</c> when it couldn't be read
    /// (Defender absent or the cmdlet unavailable). Read-only and works unelevated; it is a reliable
    /// signal (it agrees with Windows Security › Dev Drive protection › "See volumes") and drives the
    /// <em>effective</em> performance-mode verdict for a detected Dev Drive.
    /// </summary>
    bool? GetDefenderPerformanceMode();
}
