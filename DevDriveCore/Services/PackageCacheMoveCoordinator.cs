using DevDriveCore.Abstractions;
using DevDriveCore.Models;
using DevDriveCore.Platform;

namespace DevDriveCore.Services;

/// <summary>
/// Thin, UI-facing orchestrator over <see cref="IPackageCacheMover"/>. It runs a single move (or
/// move-back), turns engine results/exceptions into a tidy <see cref="CacheMoveOutcome"/>, and answers
/// "can this cache be moved back?" by consulting the shared <see cref="IReversibilityStore"/> — which
/// is what makes move-back survive an app restart.
/// </summary>
/// <remarks>
/// This is the composition seam the app wires up (M4). <see cref="CreateDefault"/> shares ONE
/// <see cref="JsonFileReversibilityStore"/> between the mover and this coordinator so a move recorded
/// in one launch is visible to <see cref="CanMoveBack"/> in the next. The coordinator never throws for
/// an expected failure: a mid-copy error (already rolled back by the engine) and cancellation are both
/// projected into a non-throwing <see cref="CacheMoveOutcome"/> so a "Move all" sweep can continue.
/// </remarks>
public sealed class PackageCacheMoveCoordinator
{
    private readonly IPackageCacheMover _mover;
    private readonly IReversibilityStore _reversibility;

    /// <summary>Creates a coordinator over an explicit mover + the same store the mover records into.</summary>
    public PackageCacheMoveCoordinator(IPackageCacheMover mover, IReversibilityStore reversibility)
    {
        _mover = mover ?? throw new ArgumentNullException(nameof(mover));
        _reversibility = reversibility ?? throw new ArgumentNullException(nameof(reversibility));
    }

    /// <summary>
    /// Composes the production coordinator: a real <see cref="PackageCacheMover"/> over the real
    /// filesystem + per-user environment, sharing ONE persistent <see cref="JsonFileReversibilityStore"/>
    /// so move-back is detectable across launches.
    /// </summary>
    public static PackageCacheMoveCoordinator CreateDefault()
    {
        var store = new JsonFileReversibilityStore(JsonFileReversibilityStore.DefaultPath);
        var mover = new PackageCacheMover(new SystemFileSystem(), new UserEnvironmentWriter(), store);
        return new PackageCacheMoveCoordinator(mover, store);
    }

    /// <summary>
    /// True when a reversible move was recorded for <paramref name="environmentVariable"/> (so the row
    /// should offer "Move back"). Survives restarts because it reads the persistent store.
    /// </summary>
    public bool CanMoveBack(string environmentVariable)
    {
        if (string.IsNullOrWhiteSpace(environmentVariable))
        {
            return false;
        }

        ReversibilityEntry? entry = _reversibility.TryGet(PackageCacheMover.ReversibilityId(environmentVariable));
        return entry is { Kind: ReversibilityKinds.PackageCacheMove };
    }

    /// <summary>
    /// Runs a single move with default (source-kept) options and live progress, projecting the result
    /// into a <see cref="CacheMoveOutcome"/>. Never throws for an expected failure: a rolled-back copy
    /// becomes <see cref="CacheMoveStatus.Failed"/> and cancellation becomes
    /// <see cref="CacheMoveStatus.Cancelled"/>.
    /// </summary>
    public async Task<CacheMoveOutcome> MoveAsync(
        PackageCacheMovePlan plan,
        IProgress<CacheMoveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        try
        {
            PackageCacheMoveReceipt receipt =
                await _mover.MoveAsync(plan, progress, CacheMoveOptions.Default, cancellationToken).ConfigureAwait(false);
            return CacheMoveOutcome.FromReceipt(receipt);
        }
        catch (OperationCanceledException)
        {
            return CacheMoveOutcome.Cancelled(plan.ToolName);
        }
        catch (Exception ex)
        {
            return CacheMoveOutcome.Failed(plan.ToolName, ex.Message);
        }
    }

    /// <summary>
    /// "Map path": points the per-user environment variable at a user-chosen <paramref name="targetPath"/>
    /// WITHOUT copying files — used to start tracking an undetected tool whose cache the user points us at.
    /// Reuses the mover's env-writer + reversibility plumbing, so "Move back" restores the prior value (and,
    /// because no ownership marker is written, preserves the user's folder). Never throws for an expected
    /// failure: cancellation becomes <see cref="CacheMoveStatus.Cancelled"/>, anything else
    /// <see cref="CacheMoveStatus.Failed"/>.
    /// </summary>
    public async Task<CacheMoveOutcome> MapPathAsync(
        string environmentVariable,
        string toolName,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        try
        {
            PackageCacheMoveReceipt receipt =
                await _mover.MapAsync(environmentVariable, toolName, targetPath, cancellationToken).ConfigureAwait(false);
            return CacheMoveOutcome.Mapped(receipt);
        }
        catch (OperationCanceledException)
        {
            return CacheMoveOutcome.Cancelled(toolName);
        }
        catch (Exception ex)
        {
            return CacheMoveOutcome.Failed(toolName, ex.Message);
        }
    }

    /// <summary>
    /// Reverts a recorded move: restores the per-user environment variable and removes the Dev Drive
    /// copy (the default move keeps the source in place, so files are never moved back). Returns
    /// <see cref="CacheMoveStatus.MovedBack"/> on success, or <see cref="CacheMoveStatus.NothingToRevert"/>
    /// when no entry was found.
    /// </summary>
    public async Task<CacheMoveOutcome> MoveBackAsync(
        string environmentVariable,
        string toolName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariable);

        try
        {
            bool reverted = await _mover
                .RevertAsync(PackageCacheMover.ReversibilityId(environmentVariable), moveFilesBack: false, cancellationToken)
                .ConfigureAwait(false);

            return reverted
                ? CacheMoveOutcome.MovedBack(toolName, environmentVariable)
                : CacheMoveOutcome.NothingToRevert(toolName, environmentVariable);
        }
        catch (OperationCanceledException)
        {
            return CacheMoveOutcome.Cancelled(toolName);
        }
        catch (Exception ex)
        {
            return CacheMoveOutcome.Failed(toolName, ex.Message);
        }
    }
}
