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
/// <paramref name="BytesFreed"/> is the size measured during the scan, not a re-measurement. There
/// is nothing left to measure once the delete succeeds, and reading free space instead would credit
/// this run with every other write the machine made while it ran.
/// </remarks>
public sealed record ReclaimItemOutcome(
    ReclaimCandidate Candidate,
    ReclaimItemStatus Status,
    long BytesFreed,
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
}

/// <summary>Progress through a reclaim run.</summary>
public sealed record ReclaimExecutionProgress(
    int Completed,
    int Total,
    string CurrentItem,
    long BytesFreedSoFar);

/// <summary>Everything a reclaim run did.</summary>
public sealed record ReclaimOutcome(IReadOnlyList<ReclaimItemOutcome> Items, bool Cancelled)
{
    public static readonly ReclaimOutcome Empty = new([], Cancelled: false);

    /// <summary>Bytes returned to the volumes, counting nested selections once.</summary>
    public long BytesFreed => Items.Where(i => i.Removed).Sum(i => i.BytesFreed);

    public int RemovedCount => Items.Count(i => i.Removed);

    public int FailedCount => Items.Count(i => i.NeedsAttention);

    public IEnumerable<ReclaimItemOutcome> Failures => Items.Where(i => i.NeedsAttention);

    /// <summary>Bytes freed per volume root, for the after-picture.</summary>
    public IReadOnlyDictionary<string, long> BytesFreedByVolume =>
        Items.Where(i => i.Removed)
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
