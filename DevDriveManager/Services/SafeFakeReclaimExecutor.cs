using DevDriveReclaim;

namespace DevDriveManager.Services;

/// <summary>
/// A reclaim executor that removes nothing. Used by the automated UI suite, which drives the same
/// Review &amp; reclaim button a user does and must not empty the developer's drive to prove the
/// button works.
/// </summary>
/// <remarks>
/// It still runs the real <see cref="ReclaimPathGuard"/> and the real overlap resolution, and it
/// still reports refusals. Those are the parts most likely to be wrong, and a fake that skipped them
/// would let the UI suite pass against a room that shows the wrong outcome for every guarded path.
/// <para>
/// It reports <see cref="ReclaimItemStatus.Recycled"/> rather than inventing a "simulated" status,
/// because the room's job under test is to render outcomes correctly — a status only the fake can
/// produce would exercise a code path a user never reaches.
/// </para>
/// </remarks>
public sealed class SafeFakeReclaimExecutor : IReclaimExecutor
{
    /// <summary>Every candidate this fake was asked to remove, in the order it was asked.</summary>
    public List<string> Removed { get; } = [];

    public Task<ReclaimOutcome> ExecuteAsync(
        IReadOnlyList<ReclaimCandidate> selection,
        IProgress<ReclaimExecutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);

        IReadOnlyList<ReclaimCandidate> roots = ReclaimOverlapResolver.Roots(selection);
        IReadOnlyDictionary<string, string> containers = ReclaimOverlapResolver.ContainerByPath(selection);
        var rootPaths = new HashSet<string>(roots.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);

        var outcomes = new List<ReclaimItemOutcome>(selection.Count);
        long freed = 0;
        int completed = 0;
        bool cancelled = false;

        // Same ordering as the real one, for the same reason: a bin candidate's path is the volume
        // root, so depth alone would put it last, after everything else had been recycled into it.
        IOrderedEnumerable<ReclaimCandidate> ordered = roots
            .OrderByDescending(c => string.Equals(c.CategoryId, "recycle-bin", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => c.Path.Length);

        var rootOutcomes = new Dictionary<string, ReclaimItemOutcome>(StringComparer.OrdinalIgnoreCase);

        foreach (ReclaimCandidate candidate in ordered)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                ReclaimItemOutcome stopped = new(
                    candidate, ReclaimItemStatus.Cancelled, 0, "not reached — the run was stopped");
                outcomes.Add(stopped);
                rootOutcomes[candidate.Path] = stopped;
                continue;
            }

            ReclaimGuardVerdict verdict = ReclaimPathGuard.Inspect(candidate);

            ReclaimItemOutcome outcome = verdict.Decision switch
            {
                ReclaimGuardDecision.Refuse =>
                    new ReclaimItemOutcome(candidate, ReclaimItemStatus.Refused, 0, verdict.Explanation),

                ReclaimGuardDecision.AlreadyGone =>
                    new ReclaimItemOutcome(
                        candidate, ReclaimItemStatus.AlreadyGone, 0,
                        "already gone — something else removed it since the scan"),

                _ => new ReclaimItemOutcome(
                    candidate,
                    ReclaimItemStatus.Recycled,
                    candidate.SizeBytes,
                    "moved to the Recycle Bin"),
            };

            if (outcome.Removed)
            {
                Removed.Add(candidate.Path);
                freed += outcome.BytesFreed;
            }

            outcomes.Add(outcome);
            rootOutcomes[candidate.Path] = outcome;
            completed++;
            progress?.Report(new ReclaimExecutionProgress(
                completed, roots.Count, candidate.DisplayName, freed));
        }

        // A child inherits its container's fate. Reporting "absorbed" unconditionally would turn a
        // guard refusal into a reported success and make the ViewModel drop rows for folders that are
        // still there.
        foreach (ReclaimCandidate candidate in selection.Where(c => !rootPaths.Contains(c.Path)))
        {
            string parent = containers.TryGetValue(candidate.Path, out string? container)
                ? container
                : "another selected item";

            bool containerRemoved =
                container is not null &&
                rootOutcomes.TryGetValue(container, out ReclaimItemOutcome? containerOutcome) &&
                containerOutcome.Removed;

            outcomes.Add(containerRemoved
                ? new ReclaimItemOutcome(candidate, ReclaimItemStatus.Absorbed, 0, $"removed with {parent}")
                : new ReclaimItemOutcome(
                    candidate,
                    cancelled ? ReclaimItemStatus.Cancelled : ReclaimItemStatus.Failed,
                    0,
                    $"still here — {parent} could not be removed"));
        }

        return Task.FromResult(new ReclaimOutcome(outcomes, cancelled));
    }
}
