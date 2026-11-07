namespace DevDriveReclaim;

/// <summary>
/// What Reclaim runs in this process moved into each volume's Recycle Bin, so the next scan can say a bin holds it.
/// <see cref="ReclaimExecutor"/> records each item it recycles and forgets a volume's items when it empties that bin;
/// <see cref="Providers.RecycleBinReclaimProvider"/> grades a bin <see cref="ReclaimRisk.Careful"/> while it holds a Careful one.
/// </summary>
/// <remarks>
/// Kept in memory only: after a restart a bin is graded <see cref="ReclaimRisk.Check"/>, which is never preselected either.
/// An item counts as still in the bin while nothing is at its original path, so one restored from the bin drops out.
/// </remarks>
public sealed class RecycledByReclaim
{
    /// <summary>The record <see cref="ReclaimExecutor"/> and <see cref="Providers.RecycleBinReclaimProvider"/> share by default.</summary>
    public static RecycledByReclaim Shared { get; } = new();

    private readonly object _gate = new();
    private readonly List<Item> _items = [];

    /// <summary>One item a Reclaim run moved to the Recycle Bin on <paramref name="VolumeRoot"/>, with the grade it had.</summary>
    public sealed record Item(string VolumeRoot, string Path, string DisplayName, ReclaimRisk Risk);

    /// <summary>Records a candidate a run moved into the Recycle Bin on <paramref name="volumeRoot"/>.</summary>
    public void Recycled(string volumeRoot, ReclaimCandidate candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            _items.RemoveAll(i => SamePath(i.Path, candidate.Path));
            _items.Add(new Item(Normalize(volumeRoot), candidate.Path, candidate.DisplayName, candidate.Risk));
        }
    }

    /// <summary>The bin on <paramref name="volumeRoot"/> was emptied, or found empty: nothing Reclaim put there is left.</summary>
    public void Emptied(string volumeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        string root = Normalize(volumeRoot);
        lock (_gate)
        {
            _items.RemoveAll(i => SamePath(i.VolumeRoot, root));
        }
    }

    /// <summary>
    /// The items Reclaim recycled on <paramref name="volumeRoot"/> that are still in its bin, the riskiest first. An item with
    /// something at its original path again (<paramref name="exists"/>; restored, or made again) is dropped.
    /// </summary>
    public IReadOnlyList<Item> In(string volumeRoot, Func<string, bool>? exists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        exists ??= System.IO.Path.Exists;
        string root = Normalize(volumeRoot);
        lock (_gate)
        {
            _items.RemoveAll(i => SamePath(i.VolumeRoot, root) && exists(i.Path));
            return [.. _items.Where(i => SamePath(i.VolumeRoot, root)).OrderByDescending(i => i.Risk)];
        }
    }

    private static string Normalize(string root) => root.TrimEnd('\\', '/') + "\\";

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
