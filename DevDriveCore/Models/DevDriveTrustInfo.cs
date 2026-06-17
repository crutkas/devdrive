namespace DevDriveCore.Models;

/// <summary>Trust state of a Dev Drive. <see cref="Unknown"/> is used when it can't be determined (e.g. unelevated).</summary>
public enum DevDriveTrustState
{
    /// <summary>Could not be determined (e.g. <c>fsutil devdrv query</c> needs elevation and was denied).</summary>
    Unknown,

    /// <summary>The Dev Drive is trusted (Defender performance mode / async scanning applies).</summary>
    Trusted,

    /// <summary>The Dev Drive is explicitly untrusted.</summary>
    Untrusted,
}

/// <summary>
/// Elevation-dependent trust + filter detail for a Dev Drive, parsed from
/// <c>fsutil devdrv query &lt;X:&gt;</c>.
/// </summary>
/// <remarks>
/// A <c>null</c> <see cref="DevDriveTrustInfo"/> (the return of
/// <see cref="DevDriveCore.Services.IDevDriveService.GetDevDriveTrustInfo"/>) means the
/// detail could not be read — almost always because the process is not elevated. In that
/// case the UI shows "Run as admin to see filters" rather than an error.
/// </remarks>
public sealed record DevDriveTrustInfo
{
    /// <summary>Parsed trust state.</summary>
    public DevDriveTrustState TrustState { get; init; } = DevDriveTrustState.Unknown;

    /// <summary>
    /// True when Microsoft Defender performance mode (asynchronous scanning) is effectively on —
    /// i.e. the antivirus filter is not allowed to attach to the volume.
    /// </summary>
    public bool PerformanceModeOn { get; init; }

    /// <summary>
    /// True when the antivirus filter's attachment is enforced by group policy on this machine —
    /// parsed from the "by group policy" phrase in <c>fsutil devdrv query</c>. When set, the user
    /// cannot flip Defender performance mode locally (it is controlled by their organization), so the
    /// UI must not offer the one-click enable and instead points to Windows Security.
    /// </summary>
    public bool AntivirusPolicyEnforced { get; init; }

    /// <summary>Filter drivers currently attached to the volume.</summary>
    public IReadOnlyList<string> AttachedFilters { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Filters allowed to attach (policy). Empty means "unknown / all". Surfaced for the M1 health
    /// section and reused by the M3 "manage allowed filters" flow.
    /// </summary>
    public IReadOnlyList<string> AllowedFilters { get; init; } = Array.Empty<string>();
}
