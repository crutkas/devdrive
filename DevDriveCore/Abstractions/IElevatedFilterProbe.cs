using DevDriveCore.Models;

namespace DevDriveCore.Abstractions;

/// <summary>
/// Reads a Dev Drive's trust + filter detail via a short-lived, UAC-elevated, <b>read-only</b> helper
/// (the "Iso app" pattern) WITHOUT restarting the whole app as administrator.
/// </summary>
/// <remarks>
/// <para>
/// <c>fsutil devdrv query &lt;X:&gt;</c> needs elevation. Rather than relaunch the entire app elevated,
/// the implementation spawns a tiny elevated helper that runs ONLY that query and hands the raw result
/// back to the still-running app, which parses it with <see cref="Services.FsutilDevDrvParser"/> and
/// populates Trust &amp; filters live.
/// </para>
/// <para>
/// SAFETY: the only privileged action is the user-initiated, UAC-gated, read-only filter query. The
/// helper performs NO mutation of any kind. <see cref="ProbeAsync"/> returns <c>null</c> when the user
/// declines UAC, the helper is unavailable, the query fails, or it times out — so the caller can keep
/// the "See Filters" affordance and show a brief "couldn't read filters" note instead of an error.
/// </para>
/// <para>
/// The seam exists so the app's See-Filters flow can be unit-tested with a fake (canned
/// <see cref="DevDriveTrustInfo"/> or canned fsutil JSON) — no real elevation or process is spawned in
/// tests.
/// </para>
/// </remarks>
public interface IElevatedFilterProbe
{
    /// <summary>
    /// Runs the elevated, read-only filter query for <paramref name="driveLetter"/> and returns the
    /// parsed trust/filter detail, or <c>null</c> when it couldn't be read (UAC declined, helper
    /// unavailable, query failed, or timed out).
    /// </summary>
    Task<DevDriveTrustInfo?> ProbeAsync(char driveLetter, CancellationToken cancellationToken = default);
}
