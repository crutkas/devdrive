using System.Collections.ObjectModel;
using DevDriveReclaim.Providers;

namespace DevDriveReclaim;

/// <summary>
/// Where a scan is allowed to look and what it is allowed to cost. Passed to every provider so the
/// engine, not the provider, owns policy — a provider that decided its own scope could quietly scan
/// the whole machine when the user asked about one drive.
/// </summary>
/// <remarks>
/// A class rather than a record because it memoizes the repository walk. Value equality over a
/// memoization cache would mean two contexts built from identical inputs compare unequal purely
/// because one of them has run a scan, which is not a distinction anyone wants to reason about.
/// </remarks>
public sealed class ReclaimScanContext
{
    private readonly object _repositoryGate = new();
    private IReadOnlyList<DiscoveredRepository>? _repositories;

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

    /// <summary>
    /// Every git repository and worktree beneath <see cref="SourceRoots"/>, walked at most once per
    /// context.
    /// </summary>
    /// <remarks>
    /// Three providers need this same answer, and the engine runs them concurrently, so before this
    /// existed the identical tree was walked three times in parallel — and, worse, through three
    /// call sites free to drift into three different definitions of "a repo".
    /// <para>
    /// The lock is taken on every call rather than double-checked outside it. An uncontended
    /// acquire is nanoseconds against a walk measured in hundreds of milliseconds, and callers ask
    /// once each, so the cheap version buys nothing and would need the field to be volatile to be
    /// correct on a weakly-ordered architecture. Blocking is the intended behaviour here: a waiter
    /// wants the one walk's result, not a second walk of its own.
    /// </para>
    /// <para>
    /// A cancelled or failed walk caches nothing, so a later call retries. This is why the memo is
    /// hand-rolled rather than a <see cref="Lazy{T}"/>, which would cache the exception forever and
    /// leave a context permanently unable to answer after a single cancellation.
    /// </para>
    /// <para>
    /// The memo is only as fresh as the context, so a context must not outlive the scan that uses
    /// it or a rescan would be blind to repositories cloned since. <c>ReclaimViewModel</c> takes a
    /// <c>Func&lt;ReclaimScanContext&gt;</c> and invokes it per scan for exactly this reason;
    /// caching the context instead of the factory would reintroduce the staleness.
    /// </para>
    /// <para>
    /// The result is wrapped rather than handed over as the walker built it. <c>Discover</c> returns
    /// a <see cref="List{T}"/> behind an <see cref="IReadOnlyList{T}"/>, which was harmless while
    /// each provider held its own copy and is not once three concurrent readers share one — a
    /// downcast, or an enumeration racing an <c>Add</c>, would corrupt the other two.
    /// </para>
    /// </remarks>
    public IReadOnlyList<DiscoveredRepository> Repositories(CancellationToken cancellationToken = default)
    {
        lock (_repositoryGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _repositories ??= new ReadOnlyCollection<DiscoveredRepository>(
                [.. RepositoryWalker.Discover(SourceRoots, cancellationToken)]);
        }
    }
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
