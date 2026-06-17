using DevDriveCore.Models;

namespace DevDriveCore.Services;

/// <summary>
/// Orchestrates a Dev-Drive-vs-system-drive disk speed comparison: runs the (real, bounded)
/// benchmark on each drive and folds the two results into a <see cref="SpeedTestComparison"/>.
/// </summary>
public interface ISpeedTestService
{
    /// <summary>
    /// Benchmarks <paramref name="systemDriveRoot"/> and <paramref name="devDriveRoot"/> (each a
    /// drive root such as <c>C:\</c> / <c>G:\</c>) on a background thread and returns the comparison.
    /// Throws when a drive lacks free space or the platform benchmark fails, so the caller can show
    /// a friendly error rather than fabricate numbers.
    /// </summary>
    Task<SpeedTestComparison> RunComparisonAsync(string systemDriveRoot, string devDriveRoot, CancellationToken cancellationToken = default);
}
