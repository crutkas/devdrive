namespace DevDriveReclaim;

/// <summary>What happened to one candidate in a reclaim run.</summary>
public enum ReclaimItemStatus
{
    /// <summary>Moved to the Recycle Bin. Reversible.</summary>
    Recycled,

    /// <summary>Permanently deleted. Not reversible.</summary>
    Deleted,

    /// <summary>A volume's Recycle Bin was emptied.</summary>
    Emptied,

    /// <summary>Inside another selected item, so it went with the parent rather than separately.</summary>
    Absorbed,

    /// <summary>Not there any more by the time the run reached it. Counts as done, frees nothing.</summary>
    AlreadyGone,

    /// <summary>The guard would not act on this path. Nothing was attempted.</summary>
    Refused,

    /// <summary>Deletion was attempted and the filesystem said no.</summary>
    Failed,

    /// <summary>The run was cancelled before reaching this item.</summary>
    Cancelled,
}

/// <summary>The result for one candidate.</summary>
/// <remarks>
/// <paramref name="Bytes"/> is the size measured during the scan, not a re-measurement. There is
/// nothing left to measure once the item is gone, and reading free space instead would credit this
/// run with every other write the machine made while it ran.
/// <para>
/// Where those bytes went is the status's business, not the number's: <see cref="BytesFreed"/> and
/// <see cref="BytesRecycled"/> split them. Recycling moves an item into <c>$Recycle.Bin</c> on the
/// same volume, so it frees nothing until the bin is emptied — counting it as freed is how a run that
/// recycled 40 GB announced 40 GB of space that the volume never got back.
/// </para>
/// </remarks>
public sealed record ReclaimItemOutcome(
    ReclaimCandidate Candidate,
    ReclaimItemStatus Status,
    long Bytes,
    string Message)
{
    /// <summary>True when the thing the user selected is gone, by any route.</summary>
    public bool Removed => Status is ReclaimItemStatus.Recycled
        or ReclaimItemStatus.Deleted
        or ReclaimItemStatus.Emptied
        or ReclaimItemStatus.Absorbed
        or ReclaimItemStatus.AlreadyGone;

    /// <summary>True when the user asked for something and did not get it.</summary>
    public bool NeedsAttention => Status is ReclaimItemStatus.Refused or ReclaimItemStatus.Failed;

    /// <summary>Bytes the volume got back now: deleted outright, or emptied from a bin.</summary>
    public long BytesFreed => Status is ReclaimItemStatus.Deleted or ReclaimItemStatus.Emptied ? Bytes : 0;

    /// <summary>Bytes moved into the Recycle Bin. Restorable, and still occupying the volume.</summary>
    public long BytesRecycled => Status is ReclaimItemStatus.Recycled ? Bytes : 0;
}

/// <summary>Progress through a reclaim run.</summary>
public sealed record ReclaimExecutionProgress(
    int Completed,
    int Total,
    string CurrentItem,
    long BytesFreedSoFar,
    long BytesRecycledSoFar = 0);

/// <summary>Everything a reclaim run did.</summary>
public sealed record ReclaimOutcome(IReadOnlyList<ReclaimItemOutcome> Items, bool Cancelled)
{
    public static readonly ReclaimOutcome Empty = new([], Cancelled: false);

    /// <summary>Bytes returned to the volumes now, counting nested selections once.</summary>
    public long BytesFreed => Items.Sum(i => i.BytesFreed);

    /// <summary>Bytes moved into a Recycle Bin. They come back when the bin is emptied, not before.</summary>
    public long BytesRecycled => Items.Sum(i => i.BytesRecycled);

    public int RemovedCount => Items.Count(i => i.Removed);

    public int FailedCount => Items.Count(i => i.NeedsAttention);

    public IEnumerable<ReclaimItemOutcome> Failures => Items.Where(i => i.NeedsAttention);

    /// <summary>Bytes freed now per volume root, for the after-picture. Recycled bytes are excluded.</summary>
    public IReadOnlyDictionary<string, long> BytesFreedByVolume =>
        Items.Where(i => i.BytesFreed > 0)
            .GroupBy(i => i.Candidate.VolumeRoot, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.BytesFreed), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Removes selected candidates.
/// </summary>
/// <remarks>
/// An interface, and not because a second production implementation is expected. It is the seam the
/// UI-test build swaps out, and having exactly one is what lets
/// <c>MutationComposition</c> guarantee that a test run cannot reach a real delete — a guarantee
/// that is only worth anything if there is no second way to get at the filesystem.
/// </remarks>
public interface IReclaimExecutor
{
    /// <summary>
    /// Removes everything in <paramref name="selection"/>, resolving nesting first so a folder inside
    /// another selected folder is not deleted twice.
    /// </summary>
    /// <remarks>
    /// Returns an outcome rather than throwing. One locked file is the normal case on a developer
    /// machine, not an exceptional one, and aborting the batch would leave the user with a partial
    /// delete and no account of it.
    /// </remarks>
    Task<ReclaimOutcome> ExecuteAsync(
        IReadOnlyList<ReclaimCandidate> selection,
        IProgress<ReclaimExecutionProgress>? progress,
        CancellationToken cancellationToken);
}
