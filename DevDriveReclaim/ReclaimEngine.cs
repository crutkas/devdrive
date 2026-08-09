using System.Collections.Immutable;
using System.Diagnostics;

namespace DevDriveReclaim;

/// <summary>Everything one provider found, plus whether it actually succeeded and what it cost.</summary>
public sealed record ReclaimCategoryResult(
    ReclaimCategory Category,
    ImmutableArray<ReclaimCandidate> Candidates,
    string? FailureReason = null,
    TimeSpan Elapsed = default,
    bool Cancelled = false)
{
    public bool Succeeded => FailureReason is null;

    /// <summary>Reclaimable bytes in this category, with nesting inside the category resolved.</summary>
    public long TotalBytes => ReclaimOverlapResolver.ReclaimableBytes(Candidates);

    public long BytesFor(ReclaimRisk risk) =>
        ReclaimOverlapResolver.ReclaimableBytes(Candidates.Where(c => c.Risk == risk));
}

/// <summary>
/// The whole reclaim picture: every category, every candidate, and the per-risk and per-volume
/// rollups the room needs to answer "what does the machine look like afterwards".
/// </summary>
public sealed class ReclaimResult
{
    public ReclaimResult(IEnumerable<ReclaimCategoryResult> categories, DateTimeOffset scannedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(categories);
        Categories = [.. categories.OrderBy(c => c.Category.Order)];
        ScannedAtUtc = scannedAtUtc;
        AllCandidates = [.. Categories.SelectMany(c => c.Candidates)];
    }

    public ImmutableArray<ReclaimCategoryResult> Categories { get; }

    public ImmutableArray<ReclaimCandidate> AllCandidates { get; }

    public DateTimeOffset ScannedAtUtc { get; }

    /// <summary>
    /// Bytes this scan can actually return, with nesting resolved — a build-output row inside a
    /// worktree row is counted once, not twice. Always use this for anything a user reads; summing
    /// <see cref="AllCandidates"/> directly overstates by whatever the overlap happens to be.
    /// </summary>
    public long TotalBytes => ReclaimOverlapResolver.ReclaimableBytes(AllCandidates);

    /// <summary>
    /// The naive sum, kept only so the gap between it and <see cref="TotalBytes"/> can be shown or
    /// asserted. Never present this number to a user.
    /// </summary>
    public long UncollapsedTotalBytes => AllCandidates.Sum(c => c.SizeBytes);

    /// <summary>Categories whose provider failed, so the UI can say the total is a floor, not a fact.</summary>
    public ImmutableArray<ReclaimCategoryResult> FailedCategories =>
        [.. Categories.Where(c => !c.Succeeded)];

    /// <summary>
    /// Categories that never finished because the user stopped the scan. Separate from
    /// <see cref="FailedCategories"/> because the two need different words: a category that failed
    /// hit something the app could not handle, while one that was cancelled was simply not reached.
    /// Both make <see cref="TotalBytes"/> a floor, but only one of them is the app's fault.
    /// </summary>
    public ImmutableArray<ReclaimCategoryResult> CancelledCategories =>
        [.. Categories.Where(c => c.Cancelled)];

    /// <summary>True when the scan ran to completion with every category checked.</summary>
    public bool Complete => Categories.All(c => c.Succeeded);

    /// <summary>Reclaimable bytes in a risk tier, nesting resolved within that tier.</summary>
    public long BytesFor(ReclaimRisk risk) =>
        ReclaimOverlapResolver.ReclaimableBytes(AllCandidates.Where(c => c.Risk == risk));

    public int CountFor(ReclaimRisk risk) =>
        AllCandidates.Count(c => c.Risk == risk);

    /// <summary>
    /// The default selection: exactly the Safe tier. Check and Careful are never preselected —
    /// a tool that pre-ticks something destructive has made the decision for the user.
    /// </summary>
    public ImmutableArray<ReclaimCandidate> DefaultSelection =>
        [.. AllCandidates.Where(c => c.Risk == ReclaimRisk.Safe)];

    /// <summary>
    /// Bytes reclaimed per volume root for a given selection, nesting resolved — the input to the
    /// before/after bars. Per-volume rather than a single grand total because "218 GB" means nothing
    /// if 200 of it is on the drive that already has room and the full drive gets back 18.
    /// </summary>
    public static IReadOnlyDictionary<string, long> BytesByVolume(IEnumerable<ReclaimCandidate> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return ReclaimOverlapResolver.ReclaimableBytesByVolume(selection);
    }
}

/// <summary>
/// Fans a scan out across every registered provider and collects the results.
/// </summary>
/// <remarks>
/// Providers run concurrently because they are almost entirely I/O-bound and independent, and a
/// serial fan-out would make the room's time-to-first-answer the sum of the slowest disks rather
/// than the max. A provider that throws is recorded as a failed category rather than taking the
/// scan down with it: losing one category is a degraded answer, losing the scan is no answer.
/// </remarks>
public sealed class ReclaimEngine(IEnumerable<IReclaimProvider> providers)
{
    private readonly ImmutableArray<IReclaimProvider> _providers = [.. providers];

    public ImmutableArray<IReclaimProvider> Providers => _providers;

    /// <summary>Categories in rail order, for building the rail before a scan has run.</summary>
    public ImmutableArray<ReclaimCategory> Categories =>
        [.. _providers.Select(p => p.Category).OrderBy(c => c.Order)];

    /// <summary>
    /// Runs every provider concurrently and returns what they found. Cancellation returns the
    /// categories that <em>did</em> finish rather than throwing them away: this scan is 174 s warm
    /// and 497 s cold, so a user who stops at 480 s has waited out almost all of it, and answering
    /// them with nothing punishes them for stopping. Unreached categories come back marked, so the
    /// room can say the total is a floor rather than presenting a partial answer as a complete one.
    /// </summary>
    public async Task<ReclaimResult> ScanAsync(
        ReclaimScanContext context,
        IProgress<ReclaimScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        IEnumerable<Task<ReclaimCategoryResult>> tasks =
            _providers.Select(p => RunProviderAsync(p, context, progress, cancellationToken));

        ReclaimCategoryResult[] results = await Task.WhenAll(tasks);
        return new ReclaimResult(results, DateTimeOffset.UtcNow);
    }

    private static async Task<ReclaimCategoryResult> RunProviderAsync(
        IReclaimProvider provider,
        ReclaimScanContext context,
        IProgress<ReclaimScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        long startedAt = Stopwatch.GetTimestamp();

        try
        {
            IReadOnlyList<ReclaimCandidate> found =
                await provider.ScanAsync(context, progress, cancellationToken);

            ImmutableArray<ReclaimCandidate> capped =
            [
                .. found
                    .Where(c => c.SizeBytes >= context.MinimumCandidateBytes)
                    .OrderByDescending(c => c.SizeBytes)
                    .Take(context.MaximumCandidatesPerCategory)
            ];

            progress?.Report(new ReclaimScanProgress(
                provider.Category.Id, $"{provider.Category.Title} complete", capped.Length));

            return new ReclaimCategoryResult(
                provider.Category, capped, Elapsed: Stopwatch.GetElapsedTime(startedAt));
        }
        catch (OperationCanceledException)
        {
            // Degraded, not fatal. Returning rather than rethrowing is what lets Task.WhenAll
            // complete and hand back the categories that finished before the user pressed Cancel.
            return new ReclaimCategoryResult(
                provider.Category, [], "Cancelled before this finished",
                Stopwatch.GetElapsedTime(startedAt), Cancelled: true);
        }
        catch (Exception exception)
        {
            // One provider hitting an unexpected filesystem or tool failure degrades that category,
            // never the whole scan. The room shows the category as unavailable and says why.
            return new ReclaimCategoryResult(
                provider.Category, [], exception.Message, Stopwatch.GetElapsedTime(startedAt));
        }
    }
}
