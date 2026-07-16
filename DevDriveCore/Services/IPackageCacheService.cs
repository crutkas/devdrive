using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Detects each known developer tool's package cache, classifies it as on-the-system-drive vs
/// on-the-Dev-Drive, computes its size on demand, and builds a SAFE preview plan for moving it.
/// Nothing here mutates the machine.
/// </summary>
public interface IPackageCacheService
{
    /// <summary>
    /// Resolves every catalogued cache (env var → else default template), records whether it exists
    /// and which drive it is on relative to <paramref name="devDriveLetter"/>. Pass <see langword="null"/>
    /// when this PC has no Dev Drive; discovery still runs, but no cache is classified as on a Dev Drive.
    /// Fast: env reads + a directory-exists probe per tool (no sizing).
    /// </summary>
    IReadOnlyList<PackageCacheInfo> GetPackageCaches(char? devDriveLetter);

    /// <summary>
    /// Computes the on-disk size of a detected cache (best-effort, capped at <paramref name="timeBudget"/>).
    /// Returns 0 for an undetected/empty cache.
    /// </summary>
    Task<ulong> CalculateSizeAsync(PackageCacheInfo cache, TimeSpan timeBudget, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a read-only <see cref="PackageCacheMovePlan"/> describing what moving the cache to the
    /// Dev Drive would do. Changes nothing — execution is a future-milestone hook.
    /// </summary>
    PackageCacheMovePlan BuildMovePlan(PackageCacheInfo cache, char devDriveLetter);
}
