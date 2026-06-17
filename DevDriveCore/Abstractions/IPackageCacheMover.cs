using DevDriveCore.Models;

namespace DevDriveCore.Abstractions;

/// <summary>
/// Per-user, reversible package-cache mover. Now WIRED into the app (M4) via
/// <see cref="DevDriveCore.Services.PackageCacheMoveCoordinator"/>.
/// </summary>
/// <remarks>
/// The real implementation (<see cref="DevDriveCore.Services.PackageCacheMover"/>) creates the target
/// folder on the Dev Drive, copies the cache (hash-verified, with progress), sets the per-user
/// environment variable, and records a reversibility entry so the move can be undone. With the
/// default <see cref="DevDriveCore.Models.CacheMoveOptions"/> the source is kept in place, so revert
/// need only restore the environment variable and delete the copy. Under the UI-test seam a SAFE
/// in-memory fake is substituted so the automated suite never touches a real cache.
/// </remarks>
public interface IPackageCacheMover
{
    /// <summary>Executes a move plan with default options and no progress reporting.</summary>
    Task ApplyAsync(PackageCacheMovePlan plan, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the move with progress reporting and tunable <paramref name="options"/>, returning a
    /// receipt describing what happened (including the idempotent "already on Dev Drive" no-op). On a
    /// mid-copy failure it rolls back the partial copy and leaves the environment variable untouched.
    /// </summary>
    Task<PackageCacheMoveReceipt> MoveAsync(
        PackageCacheMovePlan plan,
        IProgress<CacheMoveProgress>? progress = null,
        CacheMoveOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// "Map path": points the per-user environment variable at <paramref name="targetPath"/> WITHOUT
    /// copying any files — used to start tracking an undetected tool whose cache the user points us at.
    /// Records a reversibility entry (so "Move back" restores the prior value), but writes NO ownership
    /// marker: the folder belongs to the user, so a later revert restores only the variable and PRESERVES
    /// the folder. This reuses the exact env-writer + reversibility plumbing the move uses; it is a new
    /// entry point, not a new engine.
    /// </summary>
    Task<PackageCacheMoveReceipt> MapAsync(
        string environmentVariable,
        string toolName,
        string targetPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverts a recorded move (by reversibility id): restores the prior per-user environment value and
    /// always removes the Dev Drive copy. When the original move deleted the source, the files are
    /// copied back to the source first (driven by the recorded
    /// <see cref="DevDriveCore.Models.ReversibilityEntry.SourceDeleted"/> state). The
    /// <paramref name="moveFilesBack"/> flag is retained for API compatibility but no longer drives the
    /// copy-back decision.
    /// </summary>
    /// <returns><c>true</c> when an entry was found and reverted; otherwise <c>false</c>.</returns>
    Task<bool> RevertAsync(string reversibilityId, bool moveFilesBack = false, CancellationToken cancellationToken = default);
}
