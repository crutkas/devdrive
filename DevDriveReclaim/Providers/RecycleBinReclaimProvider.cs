using System.Runtime.InteropServices;

namespace DevDriveReclaim.Providers;

/// <summary>
/// Reports what each volume's Recycle Bin is holding, via <c>SHQueryRecycleBinW</c>.
/// </summary>
/// <remarks>
/// Queried per volume rather than as one machine-wide number because the Recycle Bin is physically
/// per-volume: emptying it frees space on the drive the files came from, and a single total would
/// promise space on a drive that never gets any.
/// <para>
/// This is the only provider whose candidates cannot themselves be recycled — they are already in
/// the bin, so <see cref="ReclaimCandidate.SupportsRecycleBin"/> is false and deletion is final.
/// That is stated in the recovery hint rather than hidden.
/// </para>
/// <para>
/// <b>A bin is never Safe.</b> Its files are already deleted, but emptying it is final, and it can hold what a Reclaim run
/// just moved there under a promise that it could be restored. So a bin is <see cref="ReclaimRisk.Check"/>, and
/// <see cref="ReclaimRisk.Careful"/> while it holds an item Reclaim recycled as Careful (<see cref="RecycledByReclaim"/>):
/// "Select safe" never ticks it, and emptying one that holds a Careful item asks for the same consent as deleting that item.
/// </para>
/// </remarks>
public sealed class RecycleBinReclaimProvider : IReclaimProvider
{
    public static readonly ReclaimCategory ReclaimCategory = new(
        "recycle-bin",
        "Recycle Bin",
        "Files you already deleted. They still occupy the drive they came from until the bin is emptied.",
        "\uE74D",
        order: 0);

    private const string RecoveryHint = "Nothing — emptying the Recycle Bin is final. Restore anything you still want first.";

    private readonly RecycledByReclaim _recycled;

    public RecycleBinReclaimProvider()
        : this(RecycledByReclaim.Shared)
    {
    }

    /// <param name="recycled">What Reclaim runs recycled, which grades a bin that holds it. Injectable for tests.</param>
    public RecycleBinReclaimProvider(RecycledByReclaim recycled)
    {
        ArgumentNullException.ThrowIfNull(recycled);
        _recycled = recycled;
    }

    public ReclaimCategory Category => ReclaimCategory;

    public Task<IReadOnlyList<ReclaimCandidate>> ScanAsync(
        ReclaimScanContext context,
        IProgress<ReclaimScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var candidates = new List<ReclaimCandidate>();

        foreach (string volumeRoot in context.VolumeRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryQuery(volumeRoot, out long bytes, out long items))
            {
                continue;
            }

            if (bytes <= 0)
            {
                _recycled.Emptied(volumeRoot);
                continue;
            }

            candidates.Add(Candidate(volumeRoot, bytes, items, _recycled.In(volumeRoot)));
        }

        progress?.Report(new ReclaimScanProgress(
            ReclaimCategory.Id, "Recycle Bin checked", candidates.Count));

        return Task.FromResult<IReadOnlyList<ReclaimCandidate>>(candidates);
    }

    /// <summary>
    /// The row for a volume's bin holding <paramref name="bytes"/> in <paramref name="items"/> items, of which
    /// <paramref name="fromReclaim"/> are what Reclaim recycled there (<see cref="RecycledByReclaim.In"/>): Careful when one
    /// of those was Careful, else Check.
    /// </summary>
    public static ReclaimCandidate Candidate(string volumeRoot, long bytes, long items, IReadOnlyList<RecycledByReclaim.Item> fromReclaim)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        ArgumentNullException.ThrowIfNull(fromReclaim);

        string letter = volumeRoot.TrimEnd('\\', '/');
        List<RecycledByReclaim.Item> careful = [.. fromReclaim.Where(i => i.Risk == ReclaimRisk.Careful)];
        (ReclaimRisk risk, string reason) = careful.Count switch
        {
            0 => (ReclaimRisk.Check,
                "These files are already deleted, but emptying the bin is final. It can hold something you, or a Reclaim run, " +
                    "just moved there, so look through it first."),
            1 => (ReclaimRisk.Careful,
                $"It holds {careful[0].DisplayName}, which Reclaim moved there and rated Careful. Emptying the bin deletes it " +
                    "for good."),
            _ => (ReclaimRisk.Careful,
                $"It holds {careful.Count:N0} items Reclaim moved there and rated Careful, such as {careful[0].DisplayName}. " +
                    "Emptying the bin deletes them for good."),
        };

        return new ReclaimCandidate(
            ReclaimCategory.Id,
            volumeRoot,
            $"Recycle Bin on {letter}",
            bytes,
            risk,
            reason,
            RecoveryHint,
            itemCount: (int)Math.Min(items, int.MaxValue),
            detail: $"{items:N0} item{(items == 1 ? string.Empty : "s")} waiting on {letter}",
            supportsRecycleBin: false);
    }

    // The interop lives in RecycleBinQuery so this provider and ReclaimExecutor cannot drift apart
    // about the layout of a struct they both depend on being right.
    private static bool TryQuery(string volumeRoot, out long bytes, out long items) =>
        RecycleBinQuery.TryQuery(volumeRoot, out bytes, out items);
}
