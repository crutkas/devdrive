namespace DevDriveReclaim;

/// <summary>
/// Where a scan is allowed to look and what it is allowed to cost. Passed to every provider so the
/// engine, not the provider, owns policy — a provider that decided its own scope could quietly scan
/// the whole machine when the user asked about one drive.
/// </summary>
public sealed record ReclaimScanContext
{
    public ReclaimScanContext(
        IReadOnlyList<string> volumeRoots,
        IReadOnlyList<string> sourceRoots,
        long minimumCandidateBytes = 64L * 1024 * 1024,
        int maximumCandidatesPerCategory = 500)
    {
        ArgumentNullException.ThrowIfNull(volumeRoots);
        ArgumentNullException.ThrowIfNull(sourceRoots);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumCandidateBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCandidatesPerCategory, 1);

        VolumeRoots = [.. volumeRoots];
        SourceRoots = [.. sourceRoots];
        MinimumCandidateBytes = minimumCandidateBytes;
        MaximumCandidatesPerCategory = maximumCandidatesPerCategory;
    }

    /// <summary>Fixed volumes in scope, e.g. <c>C:\</c> and <c>G:\</c>.</summary>
    public IReadOnlyList<string> VolumeRoots { get; }

    /// <summary>
    /// Folders that hold code — where repos, build outputs, and worktrees live. Kept separate from
    /// <see cref="VolumeRoots"/> because walking an entire volume to find <c>bin</c> folders costs
    /// minutes, while walking the two folders a developer actually clones into costs seconds.
    /// </summary>
    public IReadOnlyList<string> SourceRoots { get; }

    /// <summary>
    /// Rows smaller than this are not worth a user's attention and are dropped. Reclaim is a
    /// decision tool, and a list of nine hundred 3 MB rows is not a decision, it is a chore.
    /// </summary>
    public long MinimumCandidateBytes { get; }

    /// <summary>Hard cap per category, so one pathological tree cannot produce an unusable table.</summary>
    public int MaximumCandidatesPerCategory { get; }
}

/// <summary>Progress from a single provider, aggregated by the engine into overall progress.</summary>
public sealed record ReclaimScanProgress(string CategoryId, string Status, int CandidatesFound);

/// <summary>
/// One reclaim detector. Everything a category needs to exist lives behind this interface, so
/// adding "crash dumps" or "Windows Update leftovers" later is a new file, not a change to the
/// engine, the ViewModel, or the room.
/// </summary>
public interface IReclaimProvider
{
    /// <summary>The category this provider fills. Also supplies the rail entry.</summary>
    ReclaimCategory Category { get; }

    /// <summary>
    /// Finds candidates within <paramref name="context"/>. Implementations must honour cancellation
    /// promptly and must never throw for an unreadable path — an inaccessible folder is a fact to
    /// skip, not a reason to fail the whole scan.
    /// </summary>
    Task<IReadOnlyList<ReclaimCandidate>> ScanAsync(
        ReclaimScanContext context,
        IProgress<ReclaimScanProgress>? progress,
        CancellationToken cancellationToken);
}
